namespace CSharpScraper.Utils;

public static class DomParser
{
    public const string ParseScript = @"
        (function() {
            const interactiveSelectors = 'a, button, input, select, textarea, [role=""button""], [role=""link""], [role=""checkbox""], [role=""radio""]';
            const allElements = document.querySelectorAll('*');
            let pgIdCounter = 1;
            const elementsData = [];
            
            // Clean up any previously assigned pg-ids
            allElements.forEach(el => {
                el.removeAttribute('data-pg-id');
            });

            function isVisible(el) {
                if (!el) return false;
                const rect = el.getBoundingClientRect();
                if (rect.width === 0 || rect.height === 0) return false;
                
                const style = window.getComputedStyle(el);
                if (style.display === 'none' || style.visibility === 'hidden' || parseFloat(style.opacity) === 0) return false;
                
                // Check ancestors
                let parent = el.parentElement;
                while (parent) {
                    const pStyle = window.getComputedStyle(parent);
                    if (pStyle.display === 'none' || pStyle.visibility === 'hidden') return false;
                    parent = parent.parentElement;
                }
                return true;
            }

            function isInteractive(el) {
                const tagName = el.tagName.toLowerCase();
                if (el.matches(interactiveSelectors)) return true;
                if (window.getComputedStyle(el).cursor === 'pointer') return true;
                if (el.hasAttribute('onclick') || el.hasAttribute('ng-click') || el.hasAttribute('v-on:click') || el.hasAttribute('@click')) return true;
                return false;
            }

            // Recurse DOM to build a clean representation
            function extractInteractive(node) {
                if (!node || node.nodeType !== Node.ELEMENT_NODE) return;
                
                const tagName = node.tagName.toLowerCase();
                // Skip script/style/noscript
                if (['script', 'style', 'noscript', 'svg', 'iframe'].includes(tagName)) return;

                if (isVisible(node) && (isInteractive(node) || ['h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'p', 'span', 'li'].includes(tagName))) {
                    const isControl = isInteractive(node);
                    const pgId = pgIdCounter++;
                    node.setAttribute('data-pg-id', pgId.toString());

                    const rect = node.getBoundingClientRect();
                    const text = (node.innerText || node.textContent || '').trim().replace(/\s+/g, ' ').substring(0, 100);
                    
                    const info = {
                        pgId: pgId,
                        tagName: tagName,
                        isControl: isControl,
                        text: text,
                        attributes: {},
                        boundingBox: {
                            x: Math.round(rect.left + window.scrollX),
                            y: Math.round(rect.top + window.scrollY),
                            width: Math.round(rect.width),
                            height: Math.round(rect.height)
                        }
                    };

                    const attrs = ['placeholder', 'type', 'name', 'value', 'href', 'role', 'aria-label', 'disabled', 'checked'];
                    attrs.forEach(attr => {
                        if (node.hasAttribute(attr)) {
                            info.attributes[attr] = node.getAttribute(attr);
                        }
                    });

                    elementsData.push(info);
                }

                // Recurse children
                for (let i = 0; i < node.children.length; i++) {
                    extractInteractive(node.children[i]);
                }
            }

            extractInteractive(document.body);
            return JSON.stringify(elementsData);
        })()
    ";

    public class ElementInfo
    {
        public int PgId { get; set; }
        public string TagName { get; set; } = string.Empty;
        public bool IsControl { get; set; }
        public string Text { get; set; } = string.Empty;
        public Dictionary<string, string> Attributes { get; set; } = new();
        public BoundingBox BoundingBox { get; set; } = new();
    }

    public class BoundingBox
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public static string FormatElementsToXml(List<ElementInfo> elements)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<page_elements>");
        foreach (var el in elements)
        {
            var attributesStr = string.Join(" ", el.Attributes.Select(kv => $"{kv.Key}=\"{kv.Value}\""));
            var hasAttrs = !string.IsNullOrWhiteSpace(attributesStr);
            var space = hasAttrs ? " " : "";

            if (el.IsControl)
            {
                sb.AppendLine($"  <{el.TagName} pg-id=\"{el.PgId}\"{space}{attributesStr} bbox=\"[{el.BoundingBox.X},{el.BoundingBox.Y},{el.BoundingBox.Width},{el.BoundingBox.Height}]\">{System.Web.HttpUtility.HtmlEncode(el.Text)}</{el.TagName}>");
            }
            else
            {
                // Only print header/structural elements if they contain meaningful text
                if (!string.IsNullOrWhiteSpace(el.Text))
                {
                    sb.AppendLine($"  <{el.TagName} pg-id=\"{el.PgId}\" bbox=\"[{el.BoundingBox.X},{el.BoundingBox.Y},{el.BoundingBox.Width},{el.BoundingBox.Height}]\">{System.Web.HttpUtility.HtmlEncode(el.Text)}</{el.TagName}>");
                }
            }
        }
        sb.AppendLine("</page_elements>");
        return sb.ToString();
    }
}
