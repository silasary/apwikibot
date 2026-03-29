using MwParserFromScratch;
using WikiClientLibrary.Pages;
using MwParserFromScratch.Nodes;
using IGDB;
using IGDB.Models;
using WikiClientLibrary.Files;
using APWikiBot;
using System.Runtime.CompilerServices;
using WikiClientLibrary.Sites;
using System.Globalization;

internal static class GamePageChecks
{
    private static readonly Dictionary<string, string> FranchiseContents = [];
    private static readonly Dictionary<string, string> TemplateRedirects = [];

    private static readonly Dictionary<string, string> GenreCategories = [];

    public static Template? FindTemplate(IEnumerable<Node> allNodes, string name)
    {
        return allNodes.OfType<Template>().FirstOrDefault(n => n.Name.ToPlainText().Trim().Equals(name, StringComparison.InvariantCultureIgnoreCase));
    }

    public static List<Template> FindTemplates(IEnumerable<Node> allNodes, string name)
    {
        return allNodes.OfType<Template>().Where(n => n.Name.ToPlainText().Trim().Equals(name, StringComparison.InvariantCultureIgnoreCase)).ToList();
    }

    public async static Task<bool> CheckTemplates(WikiPage member)
    {
        //Console.WriteLine($"Checking {member.Title} templates");
        await member.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);

        var parser = new WikitextParser();
        var ast = parser.Parse(member.Content);

        var allNodes = ast.EnumDescendants().ToList();

        var headerParagraph = allNodes.OfType<Paragraph>().FirstOrDefault();

        var infobox = FindTemplate(allNodes, "Infobox game");
        var asboxes = FindTemplates(allNodes, "asbox");
        var gamestub = FindTemplate(allNodes, "Game stub");
        var notracker = FindTemplate(allNodes, "NoTracker");

        var newContent = ast.ToString();
        foreach (var template in allNodes.OfType<Template>())
        {
            if (template.IsMagicWord)
                continue;

            var templateName = template.Name.ToPlainText().Trim();
            switch (templateName)
            {
                case "asbox":
                    continue;
            }

            if (!TemplateRedirects.TryGetValue(templateName, out var correctName))
            {
                var tempPage = new WikiPage(member.Site, "Template:" + templateName);
                await tempPage.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);
                TemplateRedirects[templateName] = correctName = tempPage.Title.Replace("Template:", "");
            }
            if (!correctName.Equals(templateName, StringComparison.InvariantCultureIgnoreCase))
            {
                newContent = newContent.Replace("{{" + templateName, "{{" + correctName);
            }
        }

        if (infobox == null)
        {
            Console.WriteLine($"{member.Title} is missing an Infobox!");
        }
        else if (infobox.Name.ToPlainText() != "Infobox game\n")
        {
            newContent = newContent.Replace("{{" + infobox.Name.ToPlainText(), "{{Infobox game\n");
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
                Summary = "Automated cleanup of templates.",
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

        var infobox = FindTemplate(allNodes, "Infobox game");
        if (infobox == null)
            return;

        var boxart = infobox.Arguments["boxart"];
        
        if (boxart == null)
        {
            Console.WriteLine($"{gamePage.Title} has no box art!");
            Game[] games = await IgdbResolution.LookupIgdb(gamePage, infobox);

            if (games.Length == 1)
            {
                var game = games.First();
                Console.WriteLine($"Found IGDB entry: {game.Slug} ({game.Id})");
                if (game.Cover == null)
                {
                    Console.WriteLine($"IGDB entry {game.Slug} has no cover.");
                    return;
                }

                var cover = await Program.IgdbClient.QueryAsync<Cover>(IGDBClient.Endpoints.Covers, $"fields *; where id = {game.Cover.Id};");
                string url = cover.First().Url;
                url = url.Replace("t_thumb", "t_cover_big").Replace(".jpg", ".png");
                string file_name = "File:" + gamePage.Title.Replace(":", "") + " Cover" + Path.GetExtension(url);

                var file = await wc.GetAsync(url);
                await gamePage.Site.UploadAsync(file_name, new StreamUploadSource(file.Content.ReadAsStream()), "Uploading box art from IGDB", false);

                Template newInfoBox = (Template)infobox.Clone();
                newInfoBox.Arguments.SetValue("boxart", $"[{file_name}]\n");
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
        }

        return;
    }

    internal static async Task CheckSupportedNavbox(WikiPage gamePage)
    {
        await gamePage.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);

        var parser = new WikitextParser();
        var ast = parser.Parse(gamePage.Content);

        var allNodes = ast.EnumDescendants().ToList();

        var infobox = FindTemplate(allNodes, "Infobox game");

        var status = infobox.Arguments["ap-status"];
        string? status_text = status?.Value?.ToPlainText()?.Trim();
        if (status == null)
        {
            Console.WriteLine($"{gamePage.Title} is missing an ap-status!");
        }
        else if (status_text == "Core-verified" || status_text == "Approved for Core")
        {
            if (!gamePage.Content.Contains("{{Navbox core}}"))
            {
                var newContent = gamePage.Content + "\n{{Navbox core}}";
                await gamePage.EditAsync(new WikiPageEditOptions()
                {
                    Summary = "Automated addition of Core Navbox for Core-verified games.",
                    Bot = true,
                    Minor = true,
                    Watch = AutoWatchBehavior.None,
                    Content = newContent,
                });
                Console.WriteLine($"Added core Navbox to {gamePage.Title}.");
            }

        }
        else if (status_text == "Custom" || status_text == "After Dark")
        {
            if (gamePage.Content.Contains("{{Navbox core}}"))
            {
                var newContent = gamePage.Content.Replace("{{Navbox core}}", "");
                await gamePage.EditAsync(new WikiPageEditOptions()
                {
                    Summary = "Automated removal of Core Navbox for Custom games.",
                    Bot = true,
                    Minor = true,
                    Watch = AutoWatchBehavior.None,
                    Content = newContent,
                });
                Console.WriteLine($"Removed Core Navbox from {gamePage.Title}.");
            }
        }
        else
        {
            Console.WriteLine($"{gamePage.Title} has unrecognized ap-status: {status_text}");
        }
    }


    internal static async Task CheckFranchiseNavbox(WikiPage gamePage)
    {
        await gamePage.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);

        var parser = new WikitextParser();
        var ast = parser.Parse(gamePage.Content);

        var allNodes = ast.EnumDescendants().ToList();

        var infobox = FindTemplate(allNodes, "Infobox game");

        var series = FindTemplate(infobox.EnumDescendants(), "Series");
        if (series != null)
        {
            var franchise_page = series.Arguments[1].Value.ToPlainText() + " (series)";
            string content;
            if (!FranchiseContents.TryGetValue(franchise_page, out content))
            {
                var franchiseWikiPage = new WikiPage(gamePage.Site, franchise_page);
                if (!franchiseWikiPage.Exists)
                {
                    Console.WriteLine($"{gamePage.Title} references non-existent franchise page {franchise_page}");
                    return;
                }
                await franchiseWikiPage.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);
                FranchiseContents[franchise_page] = franchiseWikiPage.Content;
                content = franchiseWikiPage.Content;
            }
            if (content == null)
            {
                return;
            }
            if (content.Contains("Navbox", StringComparison.InvariantCultureIgnoreCase))
            {
                var franchise_nodes = parser.Parse(content);
                var templates = franchise_nodes.EnumDescendants().OfType<Template>().ToArray();
                var navbox = templates.FirstOrDefault(t => t.Name.ToPlainText().Contains("Navbox", StringComparison.InvariantCultureIgnoreCase));

                if (!gamePage.Content.Contains(navbox.ToString(), StringComparison.InvariantCultureIgnoreCase))
                {
                    var newcontent = gamePage.Content + "\n\n" + navbox.ToString();
                    await gamePage.EditAsync(new WikiPageEditOptions
                    {
                        Bot = true,
                        Content = newcontent,
                        Summary = $"Added {navbox.Name}"
                    });
                }
            }
        }
    }

    internal static async Task CheckGenreCategories(WikiPage gamePage)
    {
        await gamePage.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);

        var parser = new WikitextParser();
        var ast = parser.Parse(gamePage.Content);

        var allNodes = ast.EnumDescendants().ToList();

        var infobox = FindTemplate(allNodes, "Infobox game");

        var genre = infobox.Arguments["genre"];
        if (genre == null)
        {
            Console.WriteLine($"{gamePage.Title} has no genre");
            return;
        }
        //Console.WriteLine(genre.ToString());
        var wps = FindTemplates(genre.Value.EnumDescendants(), "wp");
        foreach (Template wp in wps)
        {
            wp.Name.Inlines.Clear();
            wp.Name.Append("genre");
        }
        var genres = FindTemplates(genre.Value.EnumDescendants(), "genre");
        if (!genres.Any())
        {
            Console.WriteLine($"{gamePage.Title} has no genre template in its genre field!");
            string? genre_text = genre?.Value?.ToPlainText()?.Trim();
            var guess = new WikiPage(gamePage.Site, "Category:" + genre_text + " games");
            await guess.RefreshAsync();
            if (guess.Exists)
            {
                genre.Value = new Wikitext(" {{genre|" + genre.Value.ToString().Trim() + "}}\n");
            }

        }
        foreach (var g in genres)
        {
            string wp_target = g.Arguments[1]?.Value?.ToPlainText()?.Trim() ?? "";
            string self_name = wp_target;
            if (g.Arguments.Count == 2)
            {
                self_name = g.Arguments[2]?.Value?.ToPlainText()?.Trim() ?? self_name;
            }
            if (!GenreCategories.ContainsKey(self_name))
            {
                GenreCategories[self_name] = self_name;
                await CreateGenreCategory(gamePage.Site, self_name, wp_target);
            }
            if (GenreCategories[self_name] != self_name)
            {
                if (g.Arguments.Count == 2)
                {
                    g.Arguments.LastNode.Remove();
                }

                var arg2 = new TemplateArgument() { Value = new Wikitext(GenreCategories[self_name]) };
                g.Arguments.Add(arg2);
            }
        }

        
        
        var newcontent = ast.ToString();
        if (ast.ToString() != gamePage.Content)
        {
            await gamePage.EditAsync(new WikiPageEditOptions
            {
                Bot = true,
                Content = newcontent,
                Summary = $"Adjusted Genre templates"
            });
        }
    }

    private async static Task<bool> CreateGenreCategory(WikiSite site, string self_name, string wp_target)
    {

        string title = "Category:" + self_name + " games";
        var category_page = new WikiPage(site, title);
        await category_page.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);
        if (!category_page.Exists)
        {
            var title_name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(self_name);
            await category_page.EditAsync(new WikiPageEditOptions
            {
                Bot = true,
                Content = $"{{{{Stub template}}}}\n\n[[wikipedia:{wp_target}|{title_name}]] games.\n\n[[Category:Games by genre]]",
                Summary = $"Creating category for the {self_name} genre.",
            });
            return false;
        }
        if (!category_page.Title.Equals(title, StringComparison.InvariantCultureIgnoreCase))
        {
            // We got redirected
            GenreCategories[self_name] = category_page.Title.Substring(9, category_page.Title.Length - 9 - 6);
            return true;
        }
        if (category_page.Content.Contains("{Stub template}", StringComparison.InvariantCultureIgnoreCase) && !category_page.Content.Contains("{{Stub template}}", StringComparison.InvariantCultureIgnoreCase))
        {
            // Whoops.
            await category_page.EditAsync(new WikiPageEditOptions
            {
                Bot = true,
                Content = category_page.Content.Replace("{Stub template}", "{{Stub template}}"),
                Summary = $"Fixing stub template formatting.",
            });
        }
        if (category_page.Content.Contains("[[Category:Games by Genre]]"))
        {
            await category_page.EditAsync(new WikiPageEditOptions
            {
                Bot = true,
                Content = category_page.Content.Replace("[[Category:Games by Genre]]", "[[Category:Games by genre]]"),
                Summary = $"Fix category.",
            });
        }
        return false;
    }

    internal static async Task CheckTheRedirect(WikiPage gamePage)
    {
        if (!gamePage.Title.StartsWith("The "))
            return;
        var shortname = gamePage.Title.Replace("The ", "");
        var shortpage = new WikiPage(gamePage.Site, shortname);

    }
}
