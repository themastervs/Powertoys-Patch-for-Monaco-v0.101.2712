using System;
using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace PyPreviewHost2
{
    internal static class SyntaxHighlighter
    {
        private static readonly IHighlightingDefinition Python = LoadDefinition(@"
<SyntaxDefinition name=""Python"" extensions="".py"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
  <Color name=""Comment"" foreground=""DarkSeaGreen"" />
  <Color name=""String"" foreground=""LightSalmon"" />
  <Color name=""Keyword"" foreground=""DeepSkyBlue"" fontWeight=""bold"" />
  <Color name=""Number"" foreground=""PaleGreen"" />
  <RuleSet>
    <Span color=""Comment"" begin=""#"" />
    <Span color=""String"" begin=""&quot;&quot;&quot;"" end=""&quot;&quot;&quot;"" multiline=""true"" />
    <Span color=""String"" begin=""'''"" end=""'''"" multiline=""true"" />
    <Span color=""String"" begin=""&quot;"" end=""&quot;"" />
    <Span color=""String"" begin=""'"" end=""'"" />
    <Keywords foreground=""DeepSkyBlue"" fontWeight=""bold"">
      <Word>and</Word><Word>as</Word><Word>assert</Word><Word>async</Word><Word>await</Word>
      <Word>break</Word><Word>class</Word><Word>continue</Word><Word>def</Word><Word>del</Word>
      <Word>elif</Word><Word>else</Word><Word>except</Word><Word>False</Word><Word>finally</Word>
      <Word>for</Word><Word>from</Word><Word>global</Word><Word>if</Word><Word>import</Word>
      <Word>in</Word><Word>is</Word><Word>lambda</Word><Word>None</Word><Word>nonlocal</Word>
      <Word>not</Word><Word>or</Word><Word>pass</Word><Word>raise</Word><Word>return</Word>
      <Word>True</Word><Word>try</Word><Word>while</Word><Word>with</Word><Word>yield</Word>
    </Keywords>
    <Rule foreground=""PaleGreen"">\b\d+(\.\d+)?\b</Rule>
  </RuleSet>
</SyntaxDefinition>");

        private static readonly IHighlightingDefinition Json = LoadDefinition(@"
<SyntaxDefinition name=""JSON"" extensions="".json"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
  <Color name=""String"" foreground=""LightSalmon"" />
  <Color name=""Keyword"" foreground=""DeepSkyBlue"" fontWeight=""bold"" />
  <Color name=""Number"" foreground=""PaleGreen"" />
  <RuleSet>
    <Span color=""String"" begin=""&quot;"" end=""&quot;"" />
    <Keywords foreground=""DeepSkyBlue"" fontWeight=""bold""><Word>true</Word><Word>false</Word><Word>null</Word></Keywords>
    <Rule foreground=""PaleGreen"">-?\b\d+(\.\d+)?([eE][+-]?\d+)?\b</Rule>
  </RuleSet>
</SyntaxDefinition>");

        private static IHighlightingDefinition LoadDefinition(string xml)
        {
            using (var stringReader = new StringReader(xml))
            using (var xmlReader = XmlReader.Create(stringReader))
            {
                return HighlightingLoader.Load(xmlReader, HighlightingManager.Instance);
            }
        }

        public static IHighlightingDefinition ForFile(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();

            switch (extension)
            {
                case ".py":
                    return Python;
                case ".json":
                    return Json;
                case ".cs":
                    return HighlightingManager.Instance.GetDefinition("C#");
                case ".xml":
                case ".xaml":
                case ".config":
                case ".csproj":
                    return HighlightingManager.Instance.GetDefinition("XML");
                case ".js":
                case ".ts":
                    return HighlightingManager.Instance.GetDefinition("JavaScript");
                case ".cpp":
                case ".h":
                case ".c":
                    return HighlightingManager.Instance.GetDefinition("C++");
                case ".html":
                case ".htm":
                    return HighlightingManager.Instance.GetDefinition("HTML");
                default:
                    return null;
            }
        }
    }
}
