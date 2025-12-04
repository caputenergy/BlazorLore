using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Extensions;
using System.Text;
using System.Text.RegularExpressions;

namespace BlazorLore.Format.Core.Parsing;

public class BlazorParser : IBlazorParser
{
    private readonly RazorProjectEngine _projectEngine;

    public BlazorParser()
    {
        var fileSystem = RazorProjectFileSystem.Create(".");
        var projectEngine = RazorProjectEngine.Create(
            RazorConfiguration.Default,
            fileSystem,
            builder =>
            {
                builder.SetRootNamespace("BlazorLore.Format");
            });
        
        _projectEngine = projectEngine;
    }

    public RazorSyntaxTree Parse(string razorContent)
    {
        var sourceDocument = RazorSourceDocument.Create(razorContent, "Component.razor");
        var codeDocument = _projectEngine.Process(sourceDocument, "Component", Array.Empty<RazorSourceDocument>(), Array.Empty<TagHelperDescriptor>());
        return codeDocument.GetSyntaxTree();
    }

    public BlazorDocument ParseDocument(string razorContent)
    {
        var document = new BlazorDocument
        {
            OriginalContent = razorContent
        };

        var parser = new SimpleRazorParser();
        document.Nodes = parser.Parse(razorContent);

        return document;
    }
}

public class SimpleRazorParser
{
    private static readonly Regex ElementRegex = new(@"<(?<tag>\w+)(?<attrs>[^>]*)>(?<content>.*?)</\k<tag>>|<(?<selfclosing>\w+)(?<selfattrs>[^>]*)/?>", RegexOptions.Singleline);
    private static readonly Regex AttributeRegex = new(@"(?<name>@?[\w-]+(?::\w+)?)\s*=\s*[""'](?<value>[^""']*)[""']|(?<boolname>@?[\w-]+(?::\w+)?)(?=\s|>|/>)", RegexOptions.IgnoreCase);
    private static readonly Regex CommentRegex = new(@"@\*(?<content>.*?)\*@", RegexOptions.Singleline);
    private static readonly Regex CodeBlockRegex = new(@"@\{(?<code>.*?)\}", RegexOptions.Singleline);
    private static readonly Regex ParenthesizedExpressionRegex = new(@"@\((?<expr>(?:[^()""']|""[^""]*""|'[^']*'|\((?:[^()""']|""[^""]*""|'[^']*')*\))*)\)", RegexOptions.Singleline);
    private static readonly Regex ExpressionRegex = new(@"@(?!code\s*\{)(?<expr>[a-zA-Z_]\w*(?:\.\w+)*(?:\([^)]*\))?)", RegexOptions.Singleline);
    private static readonly Regex CodeDirectiveRegex = new(@"@code\s*\{(?<code>(?:[^{}]|\{(?:[^{}]|\{[^{}]*\})*\})*)\}", RegexOptions.Singleline);
    private static readonly Regex DirectiveRegex = new(@"@(?<directive>page|using|inject|inherits|layout|implements|rendermode|attribute)\s+(?<value>.*?)$", RegexOptions.Multiline);
    private static readonly Regex IfBlockRegex = new(@"@if\s*\(", RegexOptions.Singleline);
    private static readonly Regex ForeachBlockRegex = new(@"@foreach\s*\(", RegexOptions.Singleline);
    private static readonly Regex ForBlockRegex = new(@"@for\s*\(", RegexOptions.Singleline);
    private static readonly Regex ElseIfBlockRegex = new(@"else\s+if\s*\(", RegexOptions.Singleline);
    private static readonly Regex ElseBlockRegex = new(@"else(?!\s+if)", RegexOptions.Singleline);

    public List<BlazorNode> Parse(string content)
    {
        var nodes = new List<BlazorNode>();
        
        // First, check for @code directive at the end
        var codeDirectiveMatch = CodeDirectiveRegex.Match(content);
        if (codeDirectiveMatch.Success)
        {
            // Parse everything before @code
            var beforeCode = content.Substring(0, codeDirectiveMatch.Index);
            nodes.AddRange(ParseContent(beforeCode));
            
            // Add the @code block - note we need to include "@code" in the Code property
            nodes.Add(new CodeBlockNode
            {
                Code = "code " + codeDirectiveMatch.Groups["code"].Value.Trim(),
                Type = CodeBlockType.Directive,
                StartPosition = codeDirectiveMatch.Index,
                EndPosition = codeDirectiveMatch.Index + codeDirectiveMatch.Length
            });
        }
        else
        {
            nodes.AddRange(ParseContent(content));
        }
        
        return nodes;
    }
    
    public List<BlazorNode> ParseContent(string content)
    {
        var nodes = new List<BlazorNode>();
        var position = 0;

        while (position < content.Length)
        {
            // Look for both @ symbols and < symbols
            var atIndex = content.IndexOf('@', position);
            var tagStartIndex = content.IndexOf('<', position);
            
            // Determine which comes first
            int nextIndex;
            bool isRazor = false;
            
            if (atIndex == -1 && tagStartIndex == -1)
            {
                // No more tags or razor expressions, add remaining content as text
                var remainingText = content.Substring(position);
                if (!string.IsNullOrWhiteSpace(remainingText))
                {
                    nodes.Add(new TextNode
                    {
                        Content = remainingText,
                        StartPosition = position,
                        EndPosition = content.Length
                    });
                }
                break;
            }
            else if (atIndex == -1)
            {
                nextIndex = tagStartIndex;
            }
            else if (tagStartIndex == -1)
            {
                nextIndex = atIndex;
                isRazor = true;
            }
            else
            {
                nextIndex = Math.Min(atIndex, tagStartIndex);
                isRazor = (nextIndex == atIndex);
            }

            // Add text before the next element
            if (nextIndex > position)
            {
                var textBefore = content.Substring(position, nextIndex - position);
                if (!string.IsNullOrWhiteSpace(textBefore))
                {
                    nodes.Add(new TextNode
                    {
                        Content = textBefore,
                        StartPosition = position,
                        EndPosition = nextIndex
                    });
                }
            }

            if (isRazor)
            {
                // Check for parenthesized expression @(...) first
                if (nextIndex + 1 < content.Length && content[nextIndex + 1] == '(')
                {
                    var (parenExpr, parenEnd) = ExtractParenthesizedExpression(content, nextIndex + 1);
                    if (!string.IsNullOrEmpty(parenExpr))
                    {
                        nodes.Add(new CodeBlockNode
                        {
                            Code = $"({parenExpr})",  // Include parentheses in the code
                            Type = CodeBlockType.Expression,
                            StartPosition = nextIndex,
                            EndPosition = parenEnd
                        });
                        position = parenEnd;
                        continue;
                    }
                }
                
                // Handle Razor expressions
                var commentMatch = CommentRegex.Match(content, nextIndex);
                var codeBlockMatch = CodeBlockRegex.Match(content, nextIndex);
                var expressionMatch = ExpressionRegex.Match(content, nextIndex);
                var directiveMatch = DirectiveRegex.Match(content, nextIndex);
                var ifBlockMatch = IfBlockRegex.Match(content, nextIndex);
                var foreachBlockMatch = ForeachBlockRegex.Match(content, nextIndex);
                var forBlockMatch = ForBlockRegex.Match(content, nextIndex);

                var razorMatches = new[]
                {
                    (match: commentMatch, type: "comment"),
                    (match: directiveMatch, type: "directive"),
                    (match: ifBlockMatch, type: "ifblock"),
                    (match: foreachBlockMatch, type: "foreachblock"),
                    (match: forBlockMatch, type: "forblock"),
                    (match: codeBlockMatch, type: "codeblock"),
                    (match: expressionMatch, type: "expression")
                }
                .Where(m => m.match.Success && m.match.Index == nextIndex)
                .OrderBy(m => m.match.Index)
                .FirstOrDefault();

                if (razorMatches.match != null && razorMatches.match.Success)
                {
                    switch (razorMatches.type)
                    {
                        case "comment":
                            nodes.Add(new CommentNode
                            {
                                Content = razorMatches.match.Groups["content"].Value,
                                StartPosition = razorMatches.match.Index,
                                EndPosition = razorMatches.match.Index + razorMatches.match.Length
                            });
                            position = razorMatches.match.Index + razorMatches.match.Length;
                            continue;
                            
                        case "directive":
                            nodes.Add(new CodeBlockNode
                            {
                                Code = $"{razorMatches.match.Groups["directive"].Value} {razorMatches.match.Groups["value"].Value}",
                                Type = CodeBlockType.Directive,
                                StartPosition = razorMatches.match.Index,
                                EndPosition = razorMatches.match.Index + razorMatches.match.Length
                            });
                            position = razorMatches.match.Index + razorMatches.match.Length;
                            continue;
                            
                        case "codeblock":
                            nodes.Add(new CodeBlockNode
                            {
                                Code = razorMatches.match.Groups["code"].Value,
                                Type = CodeBlockType.CodeBlock,
                                StartPosition = razorMatches.match.Index,
                                EndPosition = razorMatches.match.Index + razorMatches.match.Length
                            });
                            position = razorMatches.match.Index + razorMatches.match.Length;
                            continue;

                        case "expression":
                            nodes.Add(new CodeBlockNode
                            {
                                Code = razorMatches.match.Groups["expr"].Value,
                                Type = CodeBlockType.Expression,
                                StartPosition = razorMatches.match.Index,
                                EndPosition = razorMatches.match.Index + razorMatches.match.Length
                            });
                            position = razorMatches.match.Index + razorMatches.match.Length;
                            continue;
                            
                        case "ifblock":
                            var (ifNodes, endPos) = ParseIfElseBlock(content, razorMatches.match.Index);
                            nodes.AddRange(ifNodes);
                            position = endPos;
                            continue;
                            
                        case "foreachblock":
                            var foreachStart = razorMatches.match.Index;
                            // Find the condition by matching parentheses
                            var foreachConditionStart = foreachStart + razorMatches.match.Length;
                            var (foreachCondition, foreachConditionEnd) = ExtractCondition(content, foreachConditionStart - 1);
                            if (!string.IsNullOrEmpty(foreachCondition))
                            {
                                var foreachBraceStart = content.IndexOf('{', foreachConditionEnd);
                                if (foreachBraceStart != -1)
                                {
                                    var foreachBraceEnd = FindMatchingBrace(content, foreachBraceStart);
                                    if (foreachBraceEnd != -1)
                                    {
                                        var foreachContent = content.Substring(foreachBraceStart + 1, foreachBraceEnd - foreachBraceStart - 1);
                                        nodes.Add(new CodeBlockNode
                                        {
                                            Code = $"foreach ({foreachCondition})\n{{\n{foreachContent}\n}}",
                                            Type = CodeBlockType.ForeachBlock,
                                            StartPosition = foreachStart,
                                            EndPosition = foreachBraceEnd + 1
                                        });
                                        position = foreachBraceEnd + 1;
                                        continue;
                                    }
                                }
                            }
                            break;
                            
                        case "forblock":
                            var forStart = razorMatches.match.Index;
                            // Find the condition by matching parentheses
                            var forConditionStart = forStart + razorMatches.match.Length;
                            var (forCondition, forConditionEnd) = ExtractCondition(content, forConditionStart - 1);
                            if (!string.IsNullOrEmpty(forCondition))
                            {
                                var forBraceStart = content.IndexOf('{', forConditionEnd);
                                if (forBraceStart != -1)
                                {
                                    var forBraceEnd = FindMatchingBrace(content, forBraceStart);
                                    if (forBraceEnd != -1)
                                    {
                                        var forContent = content.Substring(forBraceStart + 1, forBraceEnd - forBraceStart - 1);
                                        nodes.Add(new CodeBlockNode
                                        {
                                            Code = $"for ({forCondition})\n{{\n{forContent}\n}}",
                                            Type = CodeBlockType.ForBlock,
                                            StartPosition = forStart,
                                            EndPosition = forBraceEnd + 1
                                        });
                                        position = forBraceEnd + 1;
                                        continue;
                                    }
                                }
                            }
                            break;
                    }
                }
                else
                {
                    // Couldn't parse as Razor expression, treat @ as text
                    nodes.Add(new TextNode
                    {
                        Content = "@",
                        StartPosition = nextIndex,
                        EndPosition = nextIndex + 1
                    });
                    position = nextIndex + 1;
                }
            }
            else
            {
                // Try to parse element manually
                var elementResult = ParseElementManually(content, tagStartIndex);
                if (elementResult != null)
                {
                    nodes.Add(elementResult.Element);
                    position = elementResult.EndPosition;
                }
                else
                {
                    // Couldn't parse as element, treat as text
                    nodes.Add(new TextNode
                    {
                        Content = content[tagStartIndex].ToString(),
                        StartPosition = tagStartIndex,
                        EndPosition = tagStartIndex + 1
                    });
                    position = tagStartIndex + 1;
                }
            }
        }

        return nodes;
    }

    private class ElementParseResult
    {
        public ElementNode Element { get; set; }
        public int EndPosition { get; set; }
    }

    private ElementParseResult? ParseElementManually(string content, int startPosition)
    {
        if (startPosition >= content.Length || content[startPosition] != '<')
            return null;

        var position = startPosition + 1;

        // Skip whitespace after <
        while (position < content.Length && char.IsWhiteSpace(content[position]))
            position++;

        // Check if we've reached the end after skipping whitespace
        if (position >= content.Length)
            return null;

        // Check for special tags like <!DOCTYPE>
        if (content[position] == '!')
        {
            // Handle special declarations like <!DOCTYPE html>
            var declarationEnd = content.IndexOf('>', position);
            if (declarationEnd != -1)
            {
                var declaration = content.Substring(startPosition, declarationEnd - startPosition + 1);
                var specialElement = new ElementNode
                {
                    TagName = declaration, // Store the entire declaration as the tag name
                    IsSelfClosing = true,
                    StartPosition = startPosition,
                    EndPosition = declarationEnd + 1,
                    Attributes = new List<AttributeNode>(),
                    Children = new List<BlazorNode>()
                };
                return new ElementParseResult { Element = specialElement, EndPosition = declarationEnd + 1 };
            }
            return null;
        }

        // Get tag name
        var tagNameStart = position;
        while (position < content.Length && (char.IsLetterOrDigit(content[position]) || content[position] == '-' || content[position] == '_'))
            position++;

        if (position == tagNameStart)
            return null; // No tag name found

        var tagName = content.Substring(tagNameStart, position - tagNameStart);

        // Parse attributes
        var attributes = new List<AttributeNode>();
        var tagEndPosition = position;
        var isSelfClosing = false;

        // Find the end of the opening tag
        while (position < content.Length)
        {
            // Skip whitespace
            while (position < content.Length && char.IsWhiteSpace(content[position]))
                position++;

            if (position >= content.Length)
                return null;

            // Check for end of tag
            if (content[position] == '>')
            {
                tagEndPosition = position + 1;
                break;
            }
            else if (position + 1 < content.Length && content[position] == '/' && content[position + 1] == '>')
            {
                isSelfClosing = true;
                tagEndPosition = position + 2;
                break;
            }
            else
            {
                // Parse attribute
                var attrResult = ParseAttributeManually(content, position);
                if (attrResult != null)
                {
                    attributes.AddRange(attrResult.Attributes);
                    position = attrResult.EndPosition;
                }
                else
                {
                    position++; // Skip unknown character
                }
            }
        }

        var element = new ElementNode
        {
            TagName = tagName,
            IsSelfClosing = isSelfClosing,
            StartPosition = startPosition,
            Attributes = attributes,
            Children = new List<BlazorNode>()
        };

        if (isSelfClosing)
        {
            element.EndPosition = tagEndPosition;
            return new ElementParseResult { Element = element, EndPosition = tagEndPosition };
        }

        // Parse content and find closing tag
        var contentStart = tagEndPosition;
        var closingTag = $"</{tagName}>";
        var depth = 1;
        var searchPosition = contentStart;

        // Look for matching closing tag, accounting for nested elements of the same type
        while (searchPosition < content.Length && depth > 0)
        {
            var nextOpenTag = content.IndexOf($"<{tagName}", searchPosition);
            var nextCloseTag = content.IndexOf(closingTag, searchPosition);

            if (nextCloseTag == -1)
                return null; // No closing tag found

            // Check if there's another opening tag before the closing tag
            if (nextOpenTag != -1 && nextOpenTag < nextCloseTag)
            {
                // Make sure it's actually a tag (followed by space, >, or /)
                var charAfterTagIndex = nextOpenTag + 1 + tagName.Length;
                if (charAfterTagIndex < content.Length)
                {
                    var nextChar = content[charAfterTagIndex];
                    if (char.IsWhiteSpace(nextChar) || nextChar == '>' || nextChar == '/')
                    {
                        depth++;
                        searchPosition = nextOpenTag + tagName.Length + 1;
                        continue;
                    }
                }
            }

            if (nextCloseTag != -1)
            {
                depth--;
                if (depth == 0)
                {
                    var innerContent = content.Substring(contentStart, nextCloseTag - contentStart);
                    element.Children = ParseContent(innerContent);
                    element.EndPosition = nextCloseTag + closingTag.Length;
                    return new ElementParseResult { Element = element, EndPosition = nextCloseTag + closingTag.Length };
                }
                searchPosition = nextCloseTag + closingTag.Length;
            }
        }

        return null; // Couldn't find matching closing tag
    }

    private class AttributeParseResult
    {
        public List<AttributeNode> Attributes { get; set; } = new List<AttributeNode>();
        public int EndPosition { get; set; }
    }

    private AttributeParseResult? ParseAttributeManually(string content, int startPosition)
    {
        var position = startPosition;
        var result = new AttributeParseResult();

        // Skip whitespace
        while (position < content.Length && char.IsWhiteSpace(content[position]))
            position++;

        if (position >= content.Length)
            return null;

        // Get attribute name
        var nameStart = position;
        while (position < content.Length && 
               (char.IsLetterOrDigit(content[position]) || 
                content[position] == '-' || 
                content[position] == '_' || 
                content[position] == ':' ||
                content[position] == '@'))
        {
            position++;
        }

        if (position == nameStart)
            return null;

        var attrName = content.Substring(nameStart, position - nameStart);

        // Skip whitespace after name
        while (position < content.Length && char.IsWhiteSpace(content[position]))
            position++;

        // Check for = sign
        if (position < content.Length && content[position] == '=')
        {
            position++; // Skip =

            // Skip whitespace after =
            while (position < content.Length && char.IsWhiteSpace(content[position]))
                position++;

            // Get quote character
            if (position < content.Length && (content[position] == '"' || content[position] == '\''))
            {
                var quoteChar = content[position];
                position++; // Skip opening quote

                var valueStart = position;
                var parenDepth = 0;
                var braceDepth = 0;
                var bracketDepth = 0;

                while (position < content.Length)
                {
                    var ch = content[position];

                    if (ch == '\\' && position + 1 < content.Length)
                    {
                        // Skip escaped character
                        position += 2;
                        continue;
                    }

                    if (ch == '(')
                    {
                        parenDepth++;
                    }
                    else if (ch == ')')
                    {
                        parenDepth--;
                    }
                    else if (ch == '{')
                    {
                        braceDepth++;
                    }
                    else if (ch == '}')
                    {
                        braceDepth--;
                    }
                    else if (ch == '[')
                    {
                        bracketDepth++;
                    }
                    else if (ch == ']')
                    {
                        bracketDepth--;
                    }
                    else if (ch == '$' && position + 1 < content.Length && 
                             (content[position + 1] == '"' || content[position + 1] == '\''))
                    {
                        // String interpolation: $" or $'
                        var interpQuoteChar = content[position + 1];
                        position += 2; // Skip $"
                        
                        // Parse until we find the closing interpolation quote
                        var interpBraceDepth = 0;
                        while (position < content.Length)
                        {
                            if (content[position] == '\\' && position + 1 < content.Length)
                            {
                                position += 2;
                                continue;
                            }
                            
                            if (content[position] == '{')
                            {
                                interpBraceDepth++;
                            }
                            else if (content[position] == '}')
                            {
                                interpBraceDepth--;
                            }
                            else if (content[position] == interpQuoteChar && interpBraceDepth == 0)
                            {
                                // Found end of interpolated string
                                position++;
                                break;
                            }
                            
                            position++;
                        }
                        continue;
                    }
                    else if (ch == quoteChar && parenDepth == 0 && braceDepth == 0 && bracketDepth == 0)
                    {
                        // Found closing quote at depth 0
                        break;
                    }

                    position++;
                }

                if (position < content.Length)
                {
                    var attrValue = content.Substring(valueStart, position - valueStart);
                    position++; // Skip closing quote

                    result.Attributes.Add(new AttributeNode
                    {
                        Name = attrName,
                        Value = attrValue,
                        IsDirective = attrName.StartsWith("@")
                    });
                    result.EndPosition = position;
                    return result;
                }
            }
        }
        else
        {
            // Boolean attribute
            result.Attributes.Add(new AttributeNode
            {
                Name = attrName,
                Value = null,
                IsDirective = attrName.StartsWith("@")
            });
            result.EndPosition = position;
            return result;
        }

        return null;
    }

    private ElementNode? ParseElement(Match match, string content)
    {
        var tagName = match.Groups["tag"].Success ? match.Groups["tag"].Value : match.Groups["selfclosing"].Value;
        var attrsText = match.Groups["attrs"].Success ? match.Groups["attrs"].Value : match.Groups["selfattrs"].Value;
        var isSelfClosing = match.Groups["selfclosing"].Success;

        var element = new ElementNode
        {
            TagName = tagName,
            IsSelfClosing = isSelfClosing,
            StartPosition = match.Index,
            EndPosition = match.Index + match.Length
        };

        element.Attributes = ParseAttributes(attrsText);

        if (!isSelfClosing && match.Groups["content"].Success)
        {
            var innerContent = match.Groups["content"].Value;
            element.Children = ParseContent(innerContent);
        }

        return element;
    }

    private List<AttributeNode> ParseAttributes(string attrsText)
    {
        var attributes = new List<AttributeNode>();
        var position = 0;

        while (position < attrsText.Length)
        {
            // Skip whitespace
            while (position < attrsText.Length && char.IsWhiteSpace(attrsText[position]))
                position++;

            if (position >= attrsText.Length)
                break;

            // Parse attribute name
            var nameStart = position;
            while (position < attrsText.Length && 
                   (char.IsLetterOrDigit(attrsText[position]) || 
                    attrsText[position] == '-' || 
                    attrsText[position] == '_' || 
                    attrsText[position] == '@' ||
                    attrsText[position] == ':'))
            {
                position++;
            }

            if (position == nameStart)
                break;

            var name = attrsText.Substring(nameStart, position - nameStart);

            // Skip whitespace
            while (position < attrsText.Length && char.IsWhiteSpace(attrsText[position]))
                position++;

            // Check for = sign
            if (position < attrsText.Length && attrsText[position] == '=')
            {
                position++; // Skip =

                // Skip whitespace
                while (position < attrsText.Length && char.IsWhiteSpace(attrsText[position]))
                    position++;

                // Parse attribute value with nested quote support
                if (position < attrsText.Length && (attrsText[position] == '"' || attrsText[position] == '\''))
                {
                    var quoteChar = attrsText[position];
                    position++; // Skip opening quote
                    var valueStart = position;
                    var parenDepth = 0;
                    var braceDepth = 0;

                    while (position < attrsText.Length)
                    {
                        var ch = attrsText[position];

                        if (ch == '\\' && position + 1 < attrsText.Length)
                        {
                            // Skip escaped character
                            position += 2;
                            continue;
                        }

                        if (ch == '(')
                        {
                            parenDepth++;
                        }
                        else if (ch == ')')
                        {
                            parenDepth--;
                        }
                        else if (ch == '{')
                        {
                            braceDepth++;
                        }
                        else if (ch == '}')
                        {
                            braceDepth--;
                        }
                        else if (ch == '$' && position + 1 < attrsText.Length && 
                                 (attrsText[position + 1] == '"' || attrsText[position + 1] == '\''))
                        {
                            // String interpolation: $" or $'
                            // Skip the $ and the quote, then find matching quote
                            var interpQuoteChar = attrsText[position + 1];
                            position += 2; // Skip $"
                            
                            // Parse until we find the closing interpolation quote
                            var interpBraceDepth = 0;
                            while (position < attrsText.Length)
                            {
                                if (attrsText[position] == '\\' && position + 1 < attrsText.Length)
                                {
                                    position += 2;
                                    continue;
                                }
                                
                                if (attrsText[position] == '{')
                                {
                                    interpBraceDepth++;
                                }
                                else if (attrsText[position] == '}')
                                {
                                    interpBraceDepth--;
                                }
                                else if (attrsText[position] == interpQuoteChar && interpBraceDepth == 0)
                                {
                                    // Found end of interpolated string
                                    position++;
                                    break;
                                }
                                
                                position++;
                            }
                            continue;
                        }
                        else if (ch == quoteChar && parenDepth == 0 && braceDepth == 0)
                        {
                            // Found closing quote at depth 0
                            break;
                        }

                        position++;
                    }

                    var value = attrsText.Substring(valueStart, position - valueStart);
                    attributes.Add(new AttributeNode
                    {
                        Name = name,
                        Value = value,
                        IsDirective = name.StartsWith("@")
                    });

                    if (position < attrsText.Length && (attrsText[position] == '"' || attrsText[position] == '\''))
                        position++; // Skip closing quote
                }
            }
            else
            {
                // Boolean attribute (no value)
                attributes.Add(new AttributeNode
                {
                    Name = name,
                    Value = null,
                    IsDirective = name.StartsWith("@")
                });
            }
        }

        return attributes;
    }
    
    private int FindMatchingBrace(string content, int openBraceIndex)
    {
        var depth = 1;
        var index = openBraceIndex + 1;
        
        while (index < content.Length && depth > 0)
        {
            if (content[index] == '{')
                depth++;
            else if (content[index] == '}')
                depth--;
                
            if (depth == 0)
                return index;
                
            index++;
        }
        
        return -1;
    }
    
    private (List<BlazorNode> nodes, int endPosition) ParseIfElseBlock(string content, int startIndex)
    {
        var nodes = new List<BlazorNode>();
        var position = startIndex;
        
        // Parse the @if block
        var ifMatch = IfBlockRegex.Match(content, position);
        if (!ifMatch.Success || ifMatch.Index != position)
            return (nodes, position);
            
        // Find the condition by matching parentheses
        var conditionStart = ifMatch.Index + ifMatch.Length;
        var (ifCondition, conditionEnd) = ExtractCondition(content, conditionStart - 1);
        if (string.IsNullOrEmpty(ifCondition))
            return (nodes, position);
            
        var braceStart = content.IndexOf('{', conditionEnd);
        if (braceStart == -1)
            return (nodes, position);
            
        var braceEnd = FindMatchingBrace(content, braceStart);
        if (braceEnd == -1)
            return (nodes, position);
            
        var ifContent = content.Substring(braceStart + 1, braceEnd - braceStart - 1);
        nodes.Add(new CodeBlockNode
        {
            Code = $"if ({ifCondition})\n{{\n{ifContent}\n}}",
            Type = CodeBlockType.IfBlock,
            StartPosition = startIndex,
            EndPosition = braceEnd + 1
        });
        
        position = braceEnd + 1;
        
        // Skip whitespace
        while (position < content.Length && char.IsWhiteSpace(content[position]))
            position++;
            
        // Check for else if or else
        while (position < content.Length)
        {
            var elseIfMatch = ElseIfBlockRegex.Match(content, position);
            var elseMatch = ElseBlockRegex.Match(content, position);
            
            if (elseIfMatch.Success && elseIfMatch.Index == position)
            {
                // Find the condition by matching parentheses
                var elseIfConditionStart = elseIfMatch.Index + elseIfMatch.Length;
                var (elseIfCondition, elseIfConditionEnd) = ExtractCondition(content, elseIfConditionStart - 1);
                if (string.IsNullOrEmpty(elseIfCondition))
                    break;
                    
                braceStart = content.IndexOf('{', elseIfConditionEnd);
                if (braceStart == -1)
                    break;
                    
                braceEnd = FindMatchingBrace(content, braceStart);
                if (braceEnd == -1)
                    break;
                    
                var elseIfContent = content.Substring(braceStart + 1, braceEnd - braceStart - 1);
                nodes.Add(new CodeBlockNode
                {
                    Code = $"else if ({elseIfCondition})\n{{\n{elseIfContent}\n}}",
                    Type = CodeBlockType.ElseIfBlock,
                    StartPosition = position,
                    EndPosition = braceEnd + 1
                });
                
                position = braceEnd + 1;
                
                // Skip whitespace
                while (position < content.Length && char.IsWhiteSpace(content[position]))
                    position++;
            }
            else if (elseMatch.Success && elseMatch.Index == position)
            {
                braceStart = content.IndexOf('{', elseMatch.Index + elseMatch.Length);
                if (braceStart == -1)
                    break;
                    
                braceEnd = FindMatchingBrace(content, braceStart);
                if (braceEnd == -1)
                    break;
                    
                var elseContent = content.Substring(braceStart + 1, braceEnd - braceStart - 1);
                nodes.Add(new CodeBlockNode
                {
                    Code = $"else\n{{\n{elseContent}\n}}",
                    Type = CodeBlockType.ElseBlock,
                    StartPosition = position,
                    EndPosition = braceEnd + 1
                });
                
                position = braceEnd + 1;
                break; // else is always the last block
            }
            else
            {
                break;
            }
        }
        
        return (nodes, position);
    }
    
    private (string condition, int endPosition) ExtractCondition(string content, int openParenIndex)
    {
        if (openParenIndex >= content.Length || content[openParenIndex] != '(')
            return (string.Empty, openParenIndex);
            
        var depth = 1;
        var index = openParenIndex + 1;
        
        while (index < content.Length && depth > 0)
        {
            if (content[index] == '(')
                depth++;
            else if (content[index] == ')')
                depth--;
                
            if (depth == 0)
            {
                var condition = content.Substring(openParenIndex + 1, index - openParenIndex - 1);
                return (condition, index + 1);
            }
                
            index++;
        }
        
        return (string.Empty, openParenIndex);
    }
    
    private (string expression, int endPosition) ExtractParenthesizedExpression(string content, int openParenIndex)
    {
        // Reuse ExtractCondition since the logic is identical
        return ExtractCondition(content, openParenIndex);
    }
}
