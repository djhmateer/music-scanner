// Music Scanner - scans a directory of music files organized as Artist/Album/Track
// and prints a summary of all artists, albums, and tracks found.
// Track filenames have their leading numbers stripped (e.g. "1-01 Dancing Queen.mp3" -> "Dancing Queen").

using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Configuration;

var musicPath = Path.Combine(Directory.GetCurrentDirectory(), "music");
var musicDir = new DirectoryInfo(musicPath);

if (!musicDir.Exists) throw new DirectoryNotFoundException($"Music directory not found: {musicPath}");

string[] musicExtensions = [".mp3", ".flac", ".m4a", ".wav", ".ogg", ".wma", ".aac"];
var trackNumberPattern = new Regex(@"^\d+[-.\s]*\d*[-.\s]+"); // matches "1-01 ", "01 ", "1.01 " etc.

// Walk the folder structure: Artist / Album / Track files
var artists = new List<Artist>();
foreach (var artistDir in musicDir.GetDirectories().OrderBy(d => d.Name))
{
    var albums = new List<Album>();
    foreach (var albumDir in artistDir.GetDirectories().OrderBy(d => d.Name))
    {
        var tracks = new List<Track>();
        foreach (var file in albumDir.GetFiles().OrderBy(f => f.Name))
        {
            if (!musicExtensions.Contains(file.Extension.ToLowerInvariant()))
                continue;

            // Strip extension and leading track number to get a clean title
            var title = trackNumberPattern.Replace(Path.GetFileNameWithoutExtension(file.Name), "").Trim();
            tracks.Add(new Track(title, file.Name));
        }
        if (tracks.Count > 0)
            albums.Add(new Album(albumDir.Name, tracks));
    }
    if (albums.Count > 0)
        artists.Add(new Artist(artistDir.Name, albums));
}


foreach (var artist in artists)
{
    Console.WriteLine(artist.Name);
    foreach (var album in artist.Albums)
    {
        Console.WriteLine($"  {album.Name} ({album.Tracks.Count} tracks)");
        foreach (var track in album.Tracks)
            Console.WriteLine($"    - {track.Title}");
    }
    Console.WriteLine();
}

// Write CSV file for pasting into a web LLM
var csvPath = Path.Combine(Directory.GetCurrentDirectory(), "albums.csv");
using (var writer = new StreamWriter(csvPath))
{
    writer.WriteLine("Artist,Album,Tracks");
    foreach (var artist in artists)
    {
        foreach (var album in artist.Albums)
        {
            var tracks = string.Join("; ", album.Tracks.Select(t => t.Title));
            writer.WriteLine($"{CsvEscape(artist.Name)},{CsvEscape(album.Name)},{CsvEscape(tracks)}");
        }
    }
}
Console.WriteLine($"CSV written to {csvPath}");

// Load API key from appsettings.json
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

var apiKey = config["AnthropicApiKey"];
if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException("Set AnthropicApiKey in appsettings.json");

Console.WriteLine("\nCategorizing tracks with Claude...\n");

// Build a lookup from normalized title to file path: "artistname|tracktitle" -> full file path
var fileLookup = new Dictionary<string, string>();
foreach (var artist in artists)
{
    foreach (var album in artist.Albums)
    {
        foreach (var track in album.Tracks)
        {
            var key = Normalize($"{artist.Name}|{track.Title}");
            var filePath = Path.Combine(musicPath, artist.Name, album.Name, track.FileName);
            fileLookup.TryAdd(key, filePath);
        }
    }
}

// Build a compact track list for the prompt
var trackList = string.Join("\n", artists.SelectMany(a =>
    a.Albums.SelectMany(al =>
        al.Tracks.Select(t => $"- {a.Name} — {t.Title}"))));

var prompt = $"""
    Here is my music collection. For each track, categorize it into one or more of these categories:
    1. Most Canonical (essential, genre-defining tracks)
    2. Most Culturally Relevant (significant cultural impact)
    3. Good Kitchen Playlist (upbeat, feel-good, easy listening)
    4. 80s Music (released in the 1980s)
    5. 90s Music (released in the 1990s)
    6. Wife Playlist
    - Songs that match the musical taste of my wife.
    - Her favourite artists include: Cat Empire, Nina Simone, Aretha Franklin, Gnarls Barkley, Pharrell Williams (Happy), modern Take That, Michael Jackson, Queen, Britney Spears, James Brown, Tina Turner, Elton John, Fatboy Slim, Flaming Lips, Billy Joel, David Bowie.
    - Prioritise songs that are upbeat, joyful, funky, soulful, sing-along, or danceable.
    - Include songs with strong groove, iconic pop hooks, or big personality.

    Tracks:
    {trackList}

    Respond with a simple list: each track on one line, followed by its categories in brackets.
    Example: Artist — Track [Canonical, Kitchen Playlist]
    Only include tracks that fit at least one category. Be selective.
    """;

var client = new AnthropicClient() { ApiKey = apiKey };
var parameters = new MessageCreateParams
{
    Model = Model.ClaudeHaiku4_5,
    MaxTokens = 4096,
    Messages = [new() { Role = Role.User, Content = prompt }]
};

// Stream the response, capturing the full text for parsing afterwards
var responseBuilder = new System.Text.StringBuilder();
await foreach (var streamEvent in client.Messages.CreateStreaming(parameters))
{
    if (streamEvent.TryPickContentBlockDelta(out var delta) &&
        delta.Delta.TryPickText(out var text))
    {
        Console.Write(text.Text);
        responseBuilder.Append(text.Text);
    }
}
Console.WriteLine();

// Parse response lines like "Artist — Track [Cat1, Cat2]" and create playlist folders
var outputPath = Path.Combine(Directory.GetCurrentDirectory(), "output");
var linePattern = new Regex(@"^(.+?)\s*[—–-]\s*(.+?)\s*\[(.+?)\]", RegexOptions.Multiline);
var matches = linePattern.Matches(responseBuilder.ToString());

// Map category short names back to folder names
var categoryFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["canonical"] = "Most Canonical",
    ["most canonical"] = "Most Canonical",
    ["culturally relevant"] = "Most Culturally Relevant",
    ["most culturally relevant"] = "Most Culturally Relevant",
    ["kitchen playlist"] = "Good Kitchen Playlist",
    ["good kitchen playlist"] = "Good Kitchen Playlist",
    ["kitchen"] = "Good Kitchen Playlist",
    ["80s"] = "80s Music",
    ["80s music"] = "80s Music",
    ["90s"] = "90s Music",
    ["90s music"] = "90s Music",
    ["wife"] = "Wife Playlist",
    ["wife playlist"] = "Wife Playlist",
};

var copiedCount = 0;
foreach (Match match in matches)
{
    var artistName = match.Groups[1].Value.Trim();
    var trackTitle = match.Groups[2].Value.Trim();
    var categories = match.Groups[3].Value.Split(',').Select(c => c.Trim());

    // Look up the actual file using normalized matching
    var key = Normalize($"{artistName}|{trackTitle}");
    if (!fileLookup.TryGetValue(key, out var sourceFile) || !File.Exists(sourceFile))
        continue;

    foreach (var category in categories)
    {
        if (!categoryFolders.TryGetValue(category, out var folderName))
            continue;

        var destDir = Path.Combine(outputPath, folderName);
        Directory.CreateDirectory(destDir);

        var destFile = Path.Combine(destDir, $"{artistName} - {trackTitle}{Path.GetExtension(sourceFile)}");
        if (!File.Exists(destFile))
        {
            File.Copy(sourceFile, destFile);
            copiedCount++;
        }
    }
}

Console.WriteLine($"\nCopied {copiedCount} tracks into {outputPath}");

// Strip non-alphanumeric chars and lowercase for fuzzy matching
static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9|]", "");

static string CsvEscape(string field) =>
    field.Contains(',') || field.Contains('"') || field.Contains('\n')
        ? $"\"{field.Replace("\"", "\"\"")}\""
        : field;

record Track(string Title, string FileName);
record Album(string Name, List<Track> Tracks);
record Artist(string Name, List<Album> Albums);
