using IGDB;
using IGDB.Models;
using MwParserFromScratch.Nodes;
using WikiClientLibrary.Pages;

internal static class IgdbResolution
{
    private static readonly Dictionary<long, string> PlatformCache = [];
    private static readonly Dictionary<long, string> GenreCache = [];

    public static async Task<Game[]> LookupIgdb(WikiPage gamePage, Template infobox)
    {
        var igdbid = infobox.Arguments["igdbid"];
        const string gameFields = "fields *; ";

        Game[] games;
        if (igdbid != null && int.TryParse(igdbid.Value.ToString().Trim(), out var id))
            games = await Program.IgdbClient.QueryAsync<Game>(IGDBClient.Endpoints.Games, gameFields + $"where id = {id};");
        else
            games = await Program.IgdbClient.QueryAsync<Game>(IGDBClient.Endpoints.Games, gameFields + $"where name = \"{gamePage.Title}\";");
        if (games.Length == 0)
        {
            games = await Program.IgdbClient.QueryAsync<Game>(IGDBClient.Endpoints.Games, gameFields + $"search \"{gamePage.Title}\";");
        }
        if (games.Length > 1)
        {
            var expected_platform = infobox.Arguments["platform"];
            if (expected_platform != null)
            {
                string platform_text = expected_platform.Value.ToPlainText().Trim();
                if (string.IsNullOrEmpty(platform_text))
                {
                    var wp = expected_platform.EnumDescendants().OfType<Template>().FirstOrDefault();
                    platform_text = wp?.Arguments[2]?.Value?.ToPlainText() ?? wp?.Arguments[1]?.Value?.ToPlainText() ?? "";
                }
                if (platform_text == "PC")
                    platform_text = "PC (Microsoft Windows)";
                if (platform_text.Equals("GameCube", StringComparison.InvariantCultureIgnoreCase))
                    platform_text = "Nintendo GameCube";

                Console.WriteLine($"Filtering IGDB results for platform: {platform_text}");
                List<Game> filtered = [];
                foreach (var g in games)
                {
                    var platformNames = await GetPlatformNames(g);
                    if (platformNames.Contains(platform_text))
                    {
                        filtered.Add(g);
                    }
                }
                if (filtered.Count == 1)
                {
                    games = filtered.ToArray();
                }
            }
        }
        if (games.Length > 1)
        {
            Console.WriteLine($"{games.Length} possible games.  Disambiguation needed.");
            await RequestIgdbDisambiguation(gamePage, games);
        }

        return games;
    }

    private static async Task<List<string>> GetPlatformNames(Game game)
    {
        List<string> platforms = [];
        if (game.Platforms == null)
            return platforms;

        foreach (var pid in game.Platforms.Ids)
        {
            if (PlatformCache.TryGetValue(pid, out var name))
            {
                platforms.Add(name);
            }
            else
            {
                var names = await Program.IgdbClient.QueryAsync<Platform>(IGDBClient.Endpoints.Platforms, $"fields name; where id = {pid};");
                name = PlatformCache[pid] = names.First().Name;
                platforms.Add(name);
            }
        }

        return platforms;
    }

    public static async Task<List<string>> GetGenreNames(Game game)
    {
        List<string> genres = [];
        if (game.Genres == null)
            return genres;

        foreach (var gid in game.Genres.Ids)
        {
            if (GenreCache.TryGetValue(gid, out var name))
            {
                genres.Add(name);
            }
            else
            {
                var names = await Program.IgdbClient.QueryAsync<Platform>(IGDBClient.Endpoints.Genres, $"fields name; where id = {gid};");
                name = GenreCache[gid] = names.First().Name;
                genres.Add(name);
            }
        }

        return genres;
    }

    private static async Task RequestIgdbDisambiguation(WikiPage gamePage, Game[] games)
    {
        var talkPage = new WikiPage(gamePage.Site, "Talk:" + gamePage.Title);
        await talkPage.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);
        if ((string.IsNullOrEmpty(talkPage.Content) || !talkPage.Content.Contains("IGDB disambiguation required")) && Program.PromptForIgdbOnTalkPage)
        {
            var text = "AP Wiki Bot was unable to automatically determine which game this page is about. Please add an <code>igdbid=</code> with the appropriate ID to the game's infobox.\n\n";
            foreach (var game in games)
            {
                List<string> platforms = await GetPlatformNames(game);
                text += $"* {game.Name} ({game.FirstReleaseDate}) for {string.Join(", ", platforms)}: <code>igdbid={game.Id}</code> ({game.Url})\n";
            }
            text += "\n~~~~";

            await talkPage.AddSectionAsync("IGDB disambiguation required", new WikiPageEditOptions()
            {
                Content = text,
            });
            Program.PromptForIgdbOnTalkPage = false;  // Only prompt once per run, because it's a bit spammy and intentionally not marked as a bot edit.
        }
    }
}