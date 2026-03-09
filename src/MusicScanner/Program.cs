// Music Scanner
// =============
// Scans a ./music directory organized as Artist/Album/Track (or Artist/Track for mixes).
// Sends the track list to Claude for categorization, then copies tracks into playlist folders
// under ./output (e.g. "Good Kitchen Playlist", "Wife Playlist").
//
// Track filenames have leading numbers stripped for display (e.g. "1-01 Dancing Queen.mp3" -> "Dancing Queen").
// Matching Claude's output back to files uses normalized prefix matching to handle variations
// like "- Remastered" or "[Live]" suffixes.

using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Configuration;

// Parse --model flag (e.g. --model sonnet), defaults to haiku
var modelArg = "haiku";
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--model")
        modelArg = args[i + 1].ToLowerInvariant();
}

var modelId = modelArg switch
{
    "haiku" => Model.ClaudeHaiku4_5,
    "sonnet" => Model.ClaudeSonnet4_6,
    "opus" => Model.ClaudeOpus4_6,
    _ => throw new ArgumentException($"Unknown model: {modelArg}. Use haiku, sonnet, or opus.")
};

Console.WriteLine($"Using model: {modelArg}");

var musicPath = Path.Combine(Directory.GetCurrentDirectory(), "music");
var musicDir = new DirectoryInfo(musicPath);

if (!musicDir.Exists) throw new DirectoryNotFoundException($"Music directory not found: {musicPath}");

string[] musicExtensions = [".mp3", ".flac", ".m4a", ".wav", ".ogg", ".wma", ".aac"];
var trackNumberPattern = new Regex(@"^\d+[-.\s]*\d*[-.\s]+"); // matches "1-01 ", "01 ", "1.01 " etc.

// Scan music files from a directory, stripping track numbers from filenames to get clean titles
List<Track> ScanTracks(DirectoryInfo dir)
{
    var tracks = new List<Track>();
    foreach (var file in dir.GetFiles().OrderBy(f => f.Name))
    {
        if (!musicExtensions.Contains(file.Extension.ToLowerInvariant()))
            continue;

        var title = trackNumberPattern.Replace(Path.GetFileNameWithoutExtension(file.Name), "").Trim();
        tracks.Add(new Track(title, file.FullName));
    }
    return tracks;
}

// Walk the folder structure: Artist/Album/Track, or Artist/Track for flat folders (mixes)
var artists = new List<Artist>();
foreach (var artistDir in musicDir.GetDirectories().OrderBy(d => d.Name))
{
    var albums = new List<Album>();

    // Standard Artist/Album/Track structure
    foreach (var albumDir in artistDir.GetDirectories().OrderBy(d => d.Name))
    {
        var tracks = ScanTracks(albumDir);
        if (tracks.Count > 0)
            albums.Add(new Album(albumDir.Name, tracks));
    }

    // Flat folders with music files directly in them (e.g. mixes, soundtracks)
    var directTracks = ScanTracks(artistDir);
    if (directTracks.Count > 0)
        albums.Add(new Album(artistDir.Name, directTracks));

    if (albums.Count > 0)
        artists.Add(new Artist(artistDir.Name, albums));
}

// Print summary to console
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

// Build a lookup: normalized "artistname|tracktitle" -> full file path
// Bracket suffixes like [Live] are stripped so both sides match the same way
var fileLookup = new Dictionary<string, string>();
foreach (var artist in artists)
{
    foreach (var album in artist.Albums)
    {
        foreach (var track in album.Tracks)
        {
            var key = NormalizeTrackKey(artist.Name, track.Title);
            fileLookup.TryAdd(key, track.FilePath);
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
    7. Kids Playlist
    - Songs that kids aged 9 and 12 would enjoy.
    - Fun, energetic, silly, or anthemic songs they can sing along to.
    - Include movie/soundtrack songs, party bangers, and anything with a big chorus or infectious beat.
    - Avoid anything with explicit or adult themes.
    8. Chill / Wind Down (mellow, relaxed, evening listening — ballads, slower tempos, reflective mood)
    9. Road Trip (high-energy singalong driving anthems — big choruses, feel-good, windows-down energy)
    10. Guilty Pleasures (cheesy, campy, or so-bad-it's-good tracks that everyone secretly loves)
    11. Workout (high BPM, aggressive energy, pump-up tracks)

    Tracks:
    {trackList}

    Respond with a simple list: each track on one line, followed by its categories in brackets.
    For each category, add a confidence score 1-100 after a colon (100 = strongest fit).
    IMPORTANT: Spread scores across the full 1-100 range within each category. Every track
    should have a unique score per category — no ties. Think of it as a ranking: the #1 track
    gets the highest score, and scores decrease from there.
    Example: Artist — Track [Canonical:82, Wife Playlist:95, Kitchen Playlist:41]
    Only include tracks that fit at least one category. Be selective.
    IMPORTANT: You MUST use ALL 11 categories listed above. Do not skip any category.
    In particular, 80s Music and 90s Music are based on release decade — use them accurately.
    """;

var client = new AnthropicClient() { ApiKey = apiKey };
var parameters = new MessageCreateParams
{
    Model = modelId,
    MaxTokens = 8192,
    Messages = [new() { Role = Role.User, Content = prompt }]
};

// Stream the response to console, capturing the full text for parsing afterwards
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

// Parse response and copy tracks into category folders
// Each line looks like: "Artist — Track [Cat1:8, Cat2:7]"
// Output goes into a model-specific folder (e.g. output/haiku/) so you can compare runs
var outputPath = Path.Combine(Directory.GetCurrentDirectory(), "output", modelArg);
if (Directory.Exists(outputPath))
    Directory.Delete(outputPath, recursive: true);
Directory.CreateDirectory(outputPath);

// Save raw response for review
File.WriteAllText(Path.Combine(outputPath, "response.txt"), responseBuilder.ToString());

var linePattern = new Regex(@"^(.+?)\s*[—–-]\s*(.+)\s+\[([^\]]+)\]\s*$", RegexOptions.Multiline);

// Map the various short names Claude might use back to consistent folder names
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
    ["kids"] = "Kids Playlist",
    ["kids playlist"] = "Kids Playlist",
    ["chill"] = "Chill",
    ["chill / wind down"] = "Chill",
    ["chill/wind down"] = "Chill",
    ["wind down"] = "Chill",
    ["road trip"] = "Road Trip",
    ["guilty pleasures"] = "Guilty Pleasures",
    ["guilty"] = "Guilty Pleasures",
    ["workout"] = "Workout",
};

// First pass: parse all matches into per-folder track lists with scores
var playlistTracks = new Dictionary<string, List<(int Score, string ArtistName, string SourceFile)>>();

foreach (Match match in linePattern.Matches(responseBuilder.ToString()))
{
    var artistName = match.Groups[1].Value.Trim();
    var trackTitle = StripBrackets(match.Groups[2].Value).Trim();
    var categories = match.Groups[3].Value.Split(',').Select(c => c.Trim());

    // Prefix match handles Claude stripping suffixes like "- Remastered" from track names
    var key = NormalizeTrackKey(artistName, trackTitle);
    var sourceFile = fileLookup.FirstOrDefault(kv => kv.Key.StartsWith(key)).Value;
    if (sourceFile is null || !File.Exists(sourceFile))
    {
        Console.WriteLine($"  WARNING: No file match for \"{artistName} — {trackTitle}\" (key: {key})");
        continue;
    }

    foreach (var rawCategory in categories)
    {
        // Split "Wife Playlist:9" into category name and score
        var parts = rawCategory.Split(':');
        var category = parts[0].Trim();
        var score = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var s) ? s : 0;

        if (!categoryFolders.TryGetValue(category, out var folderName))
        {
            Console.WriteLine($"  WARNING: Unknown category \"{category}\" for {artistName} — {trackTitle}");
            continue;
        }

        if (!playlistTracks.ContainsKey(folderName))
            playlistTracks[folderName] = [];

        playlistTracks[folderName].Add((score, artistName, sourceFile));
    }
}

// Second pass: copy tracks into folders, sorted by score (highest first)
// Within the same score, tracks are interleaved by artist (round-robin) for variety
var copiedCount = 0;
foreach (var (folderName, tracks) in playlistTracks)
{
    var destDir = Path.Combine(outputPath, folderName);
    Directory.CreateDirectory(destDir);

    // Group by score, then interleave artists within each score group
    var sorted = tracks
        .GroupBy(t => t.Score)
        .OrderByDescending(g => g.Key)
        .SelectMany(InterleaveByArtist);

    var rank = 1;
    foreach (var (score, artistName, sourceFile) in sorted)
    {
        var destFile = Path.Combine(destDir, $"{rank:D2} {artistName} - {Path.GetFileName(sourceFile)}");
        if (!File.Exists(destFile))
        {
            File.Copy(sourceFile, destFile);
            copiedCount++;
        }
        rank++;
    }
}

// Round-robin tracks by artist so no artist dominates a block of sequential numbers
static IEnumerable<(int Score, string ArtistName, string SourceFile)> InterleaveByArtist(
    IGrouping<int, (int Score, string ArtistName, string SourceFile)> group)
{
    var byArtist = group.GroupBy(t => t.ArtistName).Select(g => g.ToList()).ToList();
    var maxCount = byArtist.Max(a => a.Count);
    for (var i = 0; i < maxCount; i++)
    {
        foreach (var artistTracks in byArtist)
        {
            if (i < artistTracks.Count)
                yield return artistTracks[i];
        }
    }
}

Console.WriteLine($"\nCopied {copiedCount} tracks into {outputPath}");

// --- Helpers ---

// Normalize to lowercase alphanumeric for fuzzy matching, keeping | as artist/track separator
static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9|]", "");

// Build a normalized lookup key, stripping [bracketed] suffixes like [Live] first
static string NormalizeTrackKey(string artist, string title) =>
    Normalize($"{artist}|{StripBrackets(title)}");

// Remove square bracket suffixes like [Live], [Remastered] from track titles
static string StripBrackets(string s) => Regex.Replace(s, @"\s*\[[^\]]*\]", "");

static string CsvEscape(string field) =>
    field.Contains(',') || field.Contains('"') || field.Contains('\n')
        ? $"\"{field.Replace("\"", "\"\"")}\""
        : field;

// Track stores the clean title and full file path for direct access
record Track(string Title, string FilePath);
record Album(string Name, List<Track> Tracks);
record Artist(string Name, List<Album> Albums);
