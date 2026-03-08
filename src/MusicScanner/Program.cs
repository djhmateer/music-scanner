using System.Text.RegularExpressions;

// Resolve music directory from command line arg or default to ./music
var musicPath = args.Length > 0 ? args[0] : Path.Combine(Directory.GetCurrentDirectory(), "music");

Console.WriteLine($"Scanning: {musicPath}");
Console.WriteLine();

var musicDir = new DirectoryInfo(musicPath);

if (!musicDir.Exists)
{
    Console.Error.WriteLine($"Music directory not found: {musicPath}");
    return 1;
}

string[] musicExtensions = [".mp3", ".flac", ".m4a", ".wav", ".ogg", ".wma", ".aac"];

// Matches leading track numbers like "1-01 ", "01 ", "1.01 "
var trackNumberPattern = new Regex(@"^\d+[-.\s]*\d*[-.\s]+");

// Walk the folder structure: Artist / Album / Track files
var artists = new List<Artist>();

foreach (var artistDir in musicDir.GetDirectories().OrderBy(d => d.Name))
{
    var albums = new List<Album>();

    foreach (var albumDir in artistDir.GetDirectories().OrderBy(d => d.Name))
    {
        // Find music files and clean up track names
        var tracks = new List<Track>();
        foreach (var file in albumDir.GetFiles().OrderBy(f => f.Name))
        {
            if (!musicExtensions.Contains(file.Extension.ToLowerInvariant()))
                continue;

            // Strip extension and leading track number to get a clean title
            var title = Path.GetFileNameWithoutExtension(file.Name);
            title = trackNumberPattern.Replace(title, "").Trim();

            tracks.Add(new Track(title, file.Name));
        }

        if (tracks.Count > 0)
            albums.Add(new Album(albumDir.Name, tracks));
    }

    if (albums.Count > 0)
        artists.Add(new Artist(artistDir.Name, albums));
}

// Print summary
var totalAlbums = artists.Sum(a => a.Albums.Count);
var totalTracks = artists.Sum(a => a.Albums.Sum(al => al.Tracks.Count));
Console.WriteLine($"Found {artists.Count} artists, {totalAlbums} albums, {totalTracks} tracks");
Console.WriteLine();

// Print each artist with their albums and tracks indented
foreach (var artist in artists)
{
    Console.WriteLine(artist.Name);

    foreach (var album in artist.Albums)
    {
        Console.WriteLine($"  {album.Name} ({album.Tracks.Count} tracks)");

        foreach (var track in album.Tracks)
        {
            Console.WriteLine($"    - {track.Title}");
        }
    }

    Console.WriteLine();
}

return 0;

// --- Models ---

record Track(string Title, string FileName);
record Album(string Name, List<Track> Tracks);
record Artist(string Name, List<Album> Albums);
