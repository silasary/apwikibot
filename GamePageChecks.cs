using MwParserFromScratch;
using WikiClientLibrary.Pages;
using System.Linq;
using MwParserFromScratch.Nodes;

internal static class GamePageChecks
{

    public async static Task<bool> CheckTemplates(WikiPage member)
    {
        Console.WriteLine($"Checking {member.Title}");
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
}