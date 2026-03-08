// Music Scanner - scans a directory of music files organized as Artist/Album/Track
// and prints a summary of all artists, albums, and tracks found.
// Track filenames have their leading numbers stripped (e.g. "1-01 Dancing Queen.mp3" -> "Dancing Queen").

using System.Text.RegularExpressions;

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


// test message

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

record Track(string Title, string FileName);
record Album(string Name, List<Track> Tracks);
record Artist(string Name, List<Album> Albums);
