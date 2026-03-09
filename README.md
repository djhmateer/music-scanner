# Music Scanner

Scans a local music collection and uses Claude to categorize tracks into playlists, ranked by confidence score.

## How it works

1. Scans the `./music` directory for audio files (Artist/Album/Track or flat folders for mixes/soundtracks)
2. Sends the full track list to Claude with playlist category definitions
3. Claude scores each track 1-100 for each category it fits
4. Copies tracks into ranked playlist folders under `./output/{model}/`

## Setup

```bash
# Add your Anthropic API key to appsettings.json
cd src/MusicScanner
cp appsettings.json.example appsettings.json  # if needed
# Set "AnthropicApiKey": "sk-ant-..."
```

## Usage

```bash
# Run with default model (Sonnet)
dotnet run --project src/MusicScanner

# Compare different models
dotnet run --project src/MusicScanner -- --model haiku
dotnet run --project src/MusicScanner -- --model sonnet
dotnet run --project src/MusicScanner -- --model opus
```

## Music directory structure

```
music/
  AC-DC/
    Back In Black/
      01 Hells Bells.mp3
      02 Shoot To Thrill.mp3
      ...
  DE Wedding Field/          # flat folder (no subfolders) treated as a mix
    01 Happy.mp3
    02 Uptown Funk.mp3
    ...
  Frozen/
    01 Let It Go.mp3
    ...
```

## Output

Each model run creates its own folder so you can compare results side by side:

```
output/
  sonnet/
    Most Canonical/
      01 AC_DC - 06 Back In Black.mp3
      02 DE Wedding Field - 01 Respect.mp3
      ...
    Wife Playlist/
      01 DE Wedding Field - 01 Happy.mp3
      ...
    response.txt              # raw Claude response for review
  haiku/
    ...
```

Tracks are numbered by rank (highest confidence score first). Within tied scores, artists are interleaved for variety.

## Categories

| Category | Description |
|---|---|
| Most Canonical | Essential, genre-defining tracks |
| Most Culturally Relevant | Significant cultural impact |
| Good Kitchen Playlist | Upbeat, feel-good, easy listening |
| 80s Music | Released in the 1980s |
| 90s Music | Released in the 1990s |
| Wife Playlist | Upbeat, joyful, funky, soulful, danceable |
| Kids Playlist | Fun for ages 9-12, singalong, no explicit content |
| Chill | Mellow, relaxed, reflective |
| Road Trip | High-energy singalong driving anthems |
| Guilty Pleasures | Cheesy, campy, so-bad-it's-good |
| Workout | High BPM, aggressive energy |

## Model comparison

| Model | Cost | Quality |
|---|---|---|
| Haiku | Cheapest | Skips categories, dumps instrumentals into Chill, may hallucinate track names |
| **Sonnet** | Mid | **Best balance. Accurate decades, well-differentiated scores, good curation** |
| Opus | Most expensive | Thorough but verbose, sometimes over-categorizes |
