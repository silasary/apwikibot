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
            const string gameFields = "fields id,name,cover,url,slug; ";

            Game[] games;
            if (igdbid != null && int.TryParse(igdbid.ToString(), out var id))
                games = await Program.IgdbClient.QueryAsync<Game>(IGDBClient.Endpoints.Games, gameFields + $"where id = {id};");
            else 
                games = await Program.IgdbClient.QueryAsync<Game>(IGDBClient.Endpoints.Games, gameFields + $"where name = \"{gamePage.Title}\";");
            if (games.Length == 1)
            {
                var game = games.First();
                Console.WriteLine($"Found IGDB entry: {game.Slug} ({game.Id})");
                
                var cover = await Program.IgdbClient.QueryAsync<Cover>(IGDBClient.Endpoints.Covers, $"fields *; where id = {game.Cover.Id};");
                string url = cover.First().Url;
                url = url.Replace("t_thumb", "t_cover_big").Replace(".jpg", ".png");
                string file_name = "File:" + gamePage.Title + " Cover" + Path.GetExtension(url);
                
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
            else
            {
                Console.WriteLine($"{games.Length} possible games.  Disabiguation needed.");
                // TODO:  Post options to Talk page, ask user to pick an IGDB id.
            }
        }

        return;

    }
}