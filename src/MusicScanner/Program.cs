// Music Scanner
// =============
// Scans a ./music directory and uses Claude to categorize tracks into playlists.
//
// Folder structure:
//   ./music/Artist/Album/Track.mp3   (standard)
//   ./music/MixName/Track.mp3        (flat folders like soundtracks or DJ mixes)
//
// Output:
//   ./output/{model}/{Category}/01 Artist - Track.mp3
//   ./output/{model}/response.txt    (raw Claude response for review)
//
// Each category folder contains tracks ranked by Claude's confidence score (1-100),
// numbered sequentially so the best tracks sort first. Within tied scores, tracks
// are interleaved round-robin by artist for variety.
//
// Usage:
//   dotnet run --project src/MusicScanner                   (defaults to sonnet)
//   dotnet run --project src/MusicScanner -- --model haiku
//   dotnet run --project src/MusicScanner -- --model opus
//
// Requires: AnthropicApiKey set in appsettings.json

using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Configuration;

// --- CLI args ---

var modelArg = "sonnet";
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

// --- Scan music directory ---

var musicPath = Path.Combine(Directory.GetCurrentDirectory(), "music");
var musicDir = new DirectoryInfo(musicPath);

if (!musicDir.Exists) throw new DirectoryNotFoundException($"Music directory not found: {musicPath}");

string[] musicExtensions = [".mp3", ".flac", ".m4a", ".wav", ".ogg", ".wma", ".aac"];
var trackNumberPattern = new Regex(@"^\d+[-.\s]*\d*[-.\s]+"); // strips "1-01 ", "01 ", "1.01 " etc.

// Extract music files from a directory, stripping leading track numbers from filenames
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

// Walk Artist/Album/Track structure, also picking up flat folders (mixes, soundtracks)
var artists = new List<Artist>();
foreach (var artistDir in musicDir.GetDirectories().OrderBy(d => d.Name))
{
    var albums = new List<Album>();

    foreach (var albumDir in artistDir.GetDirectories().OrderBy(d => d.Name))
    {
        var tracks = ScanTracks(albumDir);
        if (tracks.Count > 0)
            albums.Add(new Album(albumDir.Name, tracks));
    }

    // Flat folders: music files sitting directly in an artist-level folder
    var directTracks = ScanTracks(artistDir);
    if (directTracks.Count > 0)
        albums.Add(new Album(artistDir.Name, directTracks));

    if (albums.Count > 0)
        artists.Add(new Artist(artistDir.Name, albums));
}

// --- Print summary ---

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

// Write CSV (useful for pasting into a web LLM for quick experiments)
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

// --- Call Claude API ---

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

var apiKey = config["AnthropicApiKey"];
if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException("Set AnthropicApiKey in appsettings.json");

Console.WriteLine("\nCategorizing tracks with Claude...\n");

// Build a lookup: normalized "artistname|tracktitle" -> full file path
// Brackets like [Live] are stripped so both the prompt output and filenames match
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

var trackList = string.Join("\n", artists.SelectMany(a =>
    a.Albums.SelectMany(al =>
        al.Tracks.Select(t => $"- {a.Name} — {t.Title}"))));

var prompt = $"""
    Categorize each track into one or more categories. Score each 1-100 (unique scores, no ties, use the full range).

    Categories:
    - Wife Playlist (upbeat, joyful, funky, soulful, sing-along, danceable — she loves Cat Empire, Nina Simone, Aretha Franklin, Gnarls Barkley, Pharrell, Michael Jackson, Queen, Britney, James Brown, Tina Turner, Elton John, Fatboy Slim, Billy Joel, Bowie)
    - Kids Playlist (for kids aged 9-12 who want bangers — upbeat, high-energy, danceable, fun tracks with a beat. No lullabies, no gentle soundtrack songs. Think party anthems, pop hits, and tracks that make you want to jump around)
    - Chill (mellow, relaxed, reflective)
    - Anthems (big singalong tracks everyone knows — massive choruses, fist-pumping, crowd unifiers)

    Tracks:
    {trackList}

    Format: Artist — Track [Category:score, Category:score]
    Only include tracks that fit at least one category. Use ALL categories.
    """;

var client = new AnthropicClient() { ApiKey = apiKey };
var parameters = new MessageCreateParams
{
    Model = modelId,
    MaxTokens = 8192,
    Messages = [new() { Role = Role.User, Content = prompt }]
};

// Stream response to console while capturing for parsing and token usage
var responseBuilder = new System.Text.StringBuilder();
long inputTokens = 0, outputTokens = 0;
await foreach (var streamEvent in client.Messages.CreateStreaming(parameters))
{
    if (streamEvent.TryPickContentBlockDelta(out var delta) &&
        delta.Delta.TryPickText(out var text))
    {
        Console.Write(text.Text);
        responseBuilder.Append(text.Text);
    }

    // Capture token usage from the final message_delta event
    if (streamEvent.TryPickDelta(out var deltaEvent) && deltaEvent.Usage is { } usage)
    {
        outputTokens = usage.OutputTokens;
    }

    // Capture input tokens from the initial message_start event
    if (streamEvent.TryPickStart(out var startEvent) && startEvent.Message.Usage is { } startUsage)
    {
        inputTokens = startUsage.InputTokens;
    }
}
Console.WriteLine();

// Calculate estimated cost based on model pricing (per million tokens)
var (inputRate, outputRate) = modelArg switch
{
    "haiku" => (1.00m, 5.00m),
    "sonnet" => (3.00m, 15.00m),
    "opus" => (15.00m, 75.00m),
    _ => (0m, 0m)
};
var cost = (inputTokens * inputRate + outputTokens * outputRate) / 1_000_000m;
Console.WriteLine($"Tokens: {inputTokens:N0} in / {outputTokens:N0} out — estimated cost: ${cost:F4}");

// --- Parse response and copy tracks into playlist folders ---

// Output goes into a model-specific folder so you can compare runs side by side
var outputPath = Path.Combine(Directory.GetCurrentDirectory(), "output", modelArg);
if (Directory.Exists(outputPath))
    Directory.Delete(outputPath, recursive: true);
Directory.CreateDirectory(outputPath);

// Save raw response for review
File.WriteAllText(Path.Combine(outputPath, "response.txt"), responseBuilder.ToString());

// Parse lines like: "Artist — Track [Category:82, Category:95]"
var linePattern = new Regex(@"^(.+?)\s*[—–-]\s*(.+)\s+\[([^\]]+)\]\s*$", RegexOptions.Multiline);

// Map the various short names Claude might use back to consistent folder names
var categoryFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["wife"] = "Wife Playlist",
    ["wife playlist"] = "Wife Playlist",
    ["kids"] = "Kids Playlist",
    ["kids playlist"] = "Kids Playlist",
    ["chill"] = "Chill",
    ["anthems"] = "Anthems",
    ["anthem"] = "Anthems",
};

// Collect tracks per category folder with their scores
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
        // Parse "Wife Playlist:95" into category name and score
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

// Copy tracks into folders, ranked by score (highest first), numbered sequentially.
// Within tied scores, artists are interleaved round-robin for variety.
var copiedCount = 0;
foreach (var (folderName, tracks) in playlistTracks)
{
    var destDir = Path.Combine(outputPath, folderName);
    Directory.CreateDirectory(destDir);

    var sorted = tracks
        .GroupBy(t => t.Score)
        .OrderByDescending(g => g.Key)
        .SelectMany(InterleaveByArtist);

    var rank = 1;
    foreach (var (score, artistName, sourceFile) in sorted)
    {
        // Replace path separators in artist names (e.g. AC/DC -> AC_DC)
        var safeArtist = artistName.Replace("/", "_").Replace("\\", "_");
        var destFile = Path.Combine(destDir, $"{rank:D2} {safeArtist} - {Path.GetFileName(sourceFile)}");
        if (!File.Exists(destFile))
        {
            File.Copy(sourceFile, destFile);
            copiedCount++;
        }
        rank++;
    }
}

Console.WriteLine($"\nCopied {copiedCount} tracks into {outputPath}");

// --- Helpers ---

// Round-robin tracks by artist within a score group so no single artist dominates a block
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

// Normalize to lowercase alphanumeric for fuzzy matching, keeping | as artist/track separator
static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9|]", "");

// Build a normalized lookup key, stripping [bracketed] suffixes like [Live] first
static string NormalizeTrackKey(string artist, string title) =>
    Normalize($"{artist}|{StripBrackets(title)}");

// Remove square bracket suffixes like [Live], [Remastered] from track titles
static string StripBrackets(string s) => Regex.Replace(s, @"\s*\[[^\]]*\]", "");

// Escape a field for CSV output
static string CsvEscape(string field) =>
    field.Contains(',') || field.Contains('"') || field.Contains('\n')
        ? $"\"{field.Replace("\"", "\"\"")}\""
        : field;

record Track(string Title, string FilePath);
record Album(string Name, List<Track> Tracks);
record Artist(string Name, List<Album> Albums);
