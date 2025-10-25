using MwParserFromScratch;
using MwParserFromScratch.Nodes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace APWikiBot
{
    public static class Extensions
    {
        public static TemplateArgument SetValue(this TemplateArgumentCollection args, string argumentName, string argumentValue)
        {
            var parser = new WikitextParser();
            return args.SetValue(
                parser.Parse(argumentName),
                parser.Parse(argumentValue)
                );
        }
    }
}
