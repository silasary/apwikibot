using MwParserFromScratch;
using WikiClientLibrary.Pages;
using System.Linq;
using MwParserFromScratch.Nodes;
using IGDB;
using IGDB.Models;
using System.Net;
using WikiClientLibrary.Files;
using APWikiBot;

internal static class GamePageChecks
{
    private static readonly Dictionary<long, string> PlatformCache = [];

    public async static Task<bool> CheckTemplates(WikiPage member)
    {
        //Console.WriteLine($"Checking {member.Title} templates");
        await member.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);

        var parser = new WikitextParser();
        var ast = parser.Parse(member.Content);

        var allNodes = ast.EnumDescendants().ToList();

        var headerParagraph = allNodes.OfType<Paragraph>().FirstOrDefault();

        var infobox = allNodes.OfType<Template>().Where(n => n.Name.ToPlainText() == "Infobox game").FirstOrDefault();
        var asboxes = allNodes.OfType<Template>().Where(n => n.Name.ToPlainText() == "asbox").ToList();
        var gamestub = allNodes.OfType<Template>().Where(n => n.Name.ToPlainText() == "Game stub").FirstOrDefault();
        var notracker = allNodes.OfType<Template>().Where(n => n.Name.ToPlainText() == "NoTracker").FirstOrDefault();

        if (infobox == null)
        {
            Console.WriteLine($"{member.Title} is missing an Infobox!");
        }
        var infoboxIndex = allNodes.IndexOf(infobox);
        var notrackerIndex = allNodes.IndexOf(notracker);
        if (notracker != null && infoboxIndex < notrackerIndex)
        {
            Console.WriteLine("NoTracker was after Infobox");
            notracker.Remove();
            headerParagraph.Prepend(notracker.ToString() + "\n");
        }
        gamestub ??= asboxes.FirstOrDefault();
        var gamestubIndex = allNodes.IndexOf(gamestub);
        if (gamestub != null && infoboxIndex > gamestubIndex)
        {
            Console.WriteLine("Infobox was after Game Stub");
            gamestub.Remove();
            headerParagraph.Append(gamestub.ToString() + "\n");
        }
        
        var newContent = ast.ToString();
        string hparatext = headerParagraph.ToString();
        string oparatext = hparatext;
        while (hparatext.Contains("\n\n"))
        {
            hparatext = hparatext.Replace("\n\n", "\n");
        }
        newContent = newContent.Replace(oparatext, hparatext);

        if (newContent != member.Content)
        {
            await member.EditAsync(new WikiPageEditOptions()
            {
                Summary = "Automated cleanup of game page layout.",
                Bot = true,
                Minor = true,
                Watch = AutoWatchBehavior.None,
                Content = newContent,
            });
            Console.WriteLine($"{member.Title} has been updated.");
        }

        return true;
    }

    internal static async Task CheckForBoxArt(WikiPage gamePage)
    {
        using var wc = new HttpClient();
        wc.BaseAddress = new Uri("https://igdb.com");
        //Console.WriteLine($"Checking {member.Title} box art");
        await gamePage.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);

        var parser = new WikitextParser();
        var ast = parser.Parse(gamePage.Content);

        var allNodes = ast.EnumDescendants().ToList();

        var infobox = allNodes.OfType<Template>().Where(n => n.Name.ToPlainText() == "Infobox game").FirstOrDefault();

        var boxart = infobox.Arguments["boxart"];
        var igdbid = infobox.Arguments["igdbid"];
        if (boxart == null)
        {
            Console.WriteLine($"{gamePage.Title} has no box art!");
            const string gameFields = "fields *; ";

            Game[] games;
            if (igdbid != null && int.TryParse(igdbid.Value.ToString().Trim(), out var id))
                games = await Program.IgdbClient.QueryAsync<Game>(IGDBClient.Endpoints.Games, gameFields + $"where id = {id};");
            else 
                games = await Program.IgdbClient.QueryAsync<Game>(IGDBClient.Endpoints.Games, gameFields + $"where name = \"{gamePage.Title}\";");
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

            if (games.Length == 1)
            {
                var game = games.First();
                Console.WriteLine($"Found IGDB entry: {game.Slug} ({game.Id})");

                var cover = await Program.IgdbClient.QueryAsync<Cover>(IGDBClient.Endpoints.Covers, $"fields *; where id = {game.Cover.Id};");
                string url = cover.First().Url;
                url = url.Replace("t_thumb", "t_cover_big").Replace(".jpg", ".png");
                string file_name = "File:" + gamePage.Title.Replace(":", "") + " Cover" + Path.GetExtension(url);

                var file = await wc.GetAsync(url);
                await gamePage.Site.UploadAsync(file_name, new StreamUploadSource(file.Content.ReadAsStream()), "Uploading box art from IGDB", false);

                Template newInfoBox = (Template)infobox.Clone();
                newInfoBox.Arguments.SetValue("boxart", $"[{file_name}]");
                var newContent = gamePage.Content.Replace(infobox.ToString(), newInfoBox.ToString());
                if (newContent != gamePage.Content)
                {
                    await gamePage.EditAsync(new WikiPageEditOptions()
                    {
                        Summary = "Automated addition of box art from IGDB.",
                        Bot = true,
                        Minor = true,
                        Watch = AutoWatchBehavior.None,
                        Content = newContent,
                    });
                    Console.WriteLine($"Added automated box art from IGDB {game.Slug}.");
                }
            }
            else if (games.Length > 0)
            {
                Console.WriteLine($"{games.Length} possible games.  Disabiguation needed.");
                // TODO:  Post options to Talk page, ask user to pick an IGDB id.
                var talkPage = new WikiPage(gamePage.Site, "Talk:" + gamePage.Title);
                await talkPage.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);
                if ((string.IsNullOrEmpty(talkPage.Content) || !talkPage.Content.Contains("IGDB disabiguation required")) && Program.PromptForIgdbOnTalkPage)
                {
                    var text = "AP Wiki Bot was unable to automatically determine which game this page is about. Please add an <code>igdbid=</code> with the appropriate ID to the game's infobox.\n\n";
                    foreach (var game in games)
                    {
                        List<string> platforms = await GetPlatformNames(game);
                        text += $"* {game.Name} ({game.FirstReleaseDate}) for {string.Join(", ", platforms)}: <code>igdbid={game.Id}</code> ({game.Url})\n";
                    }
                    text += "\n~~~~";

                    await talkPage.AddSectionAsync("IGDB disabiguation required", new WikiPageEditOptions()
                    {
                        Content = text,
                    });
                    Program.PromptForIgdbOnTalkPage = false;  // Only prompt once per run, because it's a bit spammy and intentionally not marked as a bot edit.
                }
            }
        }

        return;
    }

    private static async Task<List<string>> GetPlatformNames(Game game)
    {
        List<string> platforms = [];
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

    internal static async Task CheckSupportedNavbox(WikiPage gamePage)
    {
        await gamePage.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);

        var parser = new WikitextParser();
        var ast = parser.Parse(gamePage.Content);

        var allNodes = ast.EnumDescendants().ToList();

        var infobox = allNodes.OfType<Template>().Where(n => n.Name.ToPlainText() == "Infobox game").FirstOrDefault();

        var status = infobox.Arguments["ap-status"];
        if (status == null)
        {
            Console.WriteLine($"{gamePage.Title} is missing an ap-status!");
        }
        else if (status.Value.ToPlainText() == "Core-verified" || status.Value.ToPlainText() == "Approved for Core")
        {
            if (!gamePage.Content.Contains("{{navbox supported}}"))
            {
                var newContent = gamePage.Content + "\n{{navbox supported}}";
                await gamePage.EditAsync(new WikiPageEditOptions()
                {
                    Summary = "Automated addition of Supported Navbox for Core-verified games.",
                    Bot = true,
                    Minor = true,
                    Watch = AutoWatchBehavior.None,
                    Content = newContent,
                });
                Console.WriteLine($"Added Supported Navbox to {gamePage.Title}.");
            }
        }
        else if (status.Value.ToPlainText() == "Custom" || status.Value.ToPlainText() == "After Dark")
        {
            if (gamePage.Content.Contains("{{navbox supported}}"))
            {
                var newContent = gamePage.Content.Replace("{{navbox supported}}", "");
                await gamePage.EditAsync(new WikiPageEditOptions()
                {
                    Summary = "Automated removal of Supported Navbox for Custom games.",
                    Bot = true,
                    Minor = true,
                    Watch = AutoWatchBehavior.None,
                    Content = newContent,
                });
                Console.WriteLine($"Removed Supported Navbox from {gamePage.Title}.");
            }
        }
        else
        {
            Console.WriteLine($"{gamePage.Title} has unrecognized ap-status: {status.Value.ToPlainText()}");
        }
    }
}