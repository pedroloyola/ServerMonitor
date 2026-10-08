using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

/// <summary>
/// Test-only Adaptive Card template expander for EXACTLY the subset the V3 templates use:
/// <list type="bullet">
/// <item><c>"$data": "${key}"</c> repeats an array element per item;</item>
/// <item><c>"$when": "${key}"</c> keeps an element only when a boolean key is true;</item>
/// <item><c>"${key}"</c> bindings in string values, also <c>${$root.key}</c>.</item>
/// <item>UI.9 C3: exactly two host expressions in <c>$when</c>: <c>${$host.hostTheme == 'dark'}</c> and
/// <c>${$host.hostTheme != 'dark'}</c>. Their behaviour on the native host was proven on the board
/// (P-theme / P-D).</item>
/// </list>
/// It is STRICT: an unknown key, a non-boolean <c>$when</c>, or any expression beyond a plain key throws.
/// The templates therefore cannot quietly rely on a feature the tests do not model.
/// <para>
/// Like the host's AdaptiveCards.Templating (Relay C0-offline, 12/12 literal), bound values are inserted
/// verbatim and never re-scanned, so a name containing <c>${…}</c> stays literal. The REAL host engine is
/// NativeAOT .NET and is confirmed only on the board (C0-online).
/// </para>
/// </summary>
internal static partial class CardTemplateHarness
{
    [GeneratedRegex(@"\$\{([^}]*)\}")]
    private static partial Regex Binding();

    [GeneratedRegex(@"^(\$root\.)?[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex PlainKey();

    public const string DarkTheme = "${$host.hostTheme == 'dark'}";
    public const string LightTheme = "${$host.hostTheme != 'dark'}";

    [ThreadStatic] private static string? _hostTheme;

    public static JsonObject Expand(string templateJson, string dataJson, string hostTheme = "dark")
    {
        var data = JsonNode.Parse(dataJson)!.AsObject();
        var template = JsonNode.Parse(templateJson)!.AsObject();
        _hostTheme = hostTheme;
        try
        {
            var expanded = ExpandObject(template, data, data);
            return expanded ?? throw new InvalidOperationException("the card root was removed by $when");
        }
        finally
        {
            _hostTheme = null;
        }
    }

    private static JsonObject? ExpandObject(JsonObject source, JsonObject scope, JsonObject root)
    {
        if (source["$when"] is JsonValue hostWhen && hostWhen.GetValue<string>() is DarkTheme or LightTheme)
        {
            var isDark = _hostTheme == "dark";
            if ((hostWhen.GetValue<string>() == DarkTheme) != isDark)
            {
                return null;
            }
        }
        else if (source["$when"] is JsonValue when)
        {
            var value = Resolve(BindingKey(when.GetValue<string>()), scope, root);
            if (value is not JsonValue v || !v.TryGetValue<bool>(out var keep))
            {
                throw new InvalidOperationException($"$when must bind a boolean: {when}");
            }

            if (!keep)
            {
                return null;
            }
        }

        var result = new JsonObject();
        foreach (var (name, child) in source)
        {
            if (name is "$when" or "$data")
            {
                continue;
            }

            result[name] = ExpandValue(child, scope, root);
        }

        return result;
    }

    private static JsonNode? ExpandValue(JsonNode? node, JsonObject scope, JsonObject root) => node switch
    {
        JsonObject obj when obj.ContainsKey("$data") => throw new InvalidOperationException("$data outside an array"),
        JsonObject obj => ExpandObject(obj, scope, root),
        JsonArray array => ExpandArray(array, scope, root),
        JsonValue value when value.TryGetValue<string>(out var text) => ExpandString(text, scope, root),
        _ => node?.DeepClone()
    };

    private static JsonArray ExpandArray(JsonArray source, JsonObject scope, JsonObject root)
    {
        var result = new JsonArray();
        foreach (var item in source)
        {
            if (item is JsonObject obj && obj["$data"] is JsonValue dataBinding)
            {
                var items = Resolve(BindingKey(dataBinding.GetValue<string>()), scope, root) as JsonArray
                    ?? throw new InvalidOperationException("$data must bind an array");
                foreach (var element in items)
                {
                    var itemScope = element as JsonObject ?? throw new InvalidOperationException("$data item is not an object");
                    if (ExpandObject(obj, itemScope, root) is { } expanded)
                    {
                        result.Add(expanded);
                    }
                }

                continue;
            }

            var expandedItem = ExpandValue(item, scope, root);
            if (item is JsonObject && expandedItem is null)
            {
                continue; // removed by $when
            }

            result.Add(expandedItem);
        }

        return result;
    }

    private static JsonNode? ExpandString(string text, JsonObject scope, JsonObject root)
    {
        var matches = Binding().Matches(text);
        if (matches.Count == 0)
        {
            return JsonValue.Create(text);
        }

        // A string that is exactly one binding keeps the bound value's JSON type (as the real templating does).
        if (matches.Count == 1 && matches[0].Value == text)
        {
            return Resolve(matches[0].Groups[1].Value, scope, root)?.DeepClone();
        }

        // Single pass, never re-scanned: bound values are inserted literally.
        return JsonValue.Create(Binding().Replace(text, m => Resolve(m.Groups[1].Value, scope, root)?.ToString() ?? string.Empty));
    }

    private static string BindingKey(string binding)
    {
        var match = Binding().Match(binding);
        if (!match.Success || match.Value != binding)
        {
            throw new InvalidOperationException($"not a single binding: {binding}");
        }

        return match.Groups[1].Value;
    }

    private static JsonNode? Resolve(string expression, JsonObject scope, JsonObject root)
    {
        if (!PlainKey().IsMatch(expression))
        {
            throw new InvalidOperationException($"unsupported template expression: {expression}");
        }

        var (container, key) = expression.StartsWith("$root.", StringComparison.Ordinal)
            ? (root, expression["$root.".Length..])
            : (scope, expression);
        if (!container.TryGetPropertyValue(key, out var value))
        {
            throw new KeyNotFoundException($"template binds unknown data key '{expression}'");
        }

        return value;
    }

    // ---- AC 1.6 shape check over the EXPANDED card (SPEC §8 test 10) -----------------------------------

    private static readonly HashSet<string> ElementTypes = new(StringComparer.Ordinal)
    {
        "TextBlock", "RichTextBlock", "ColumnSet", "Container", "ActionSet", "Image"
    };

    private static readonly Dictionary<string, string[]> Enums = new(StringComparer.Ordinal)
    {
        ["size"] = ["Small", "Default", "Medium", "Large", "ExtraLarge"],
        ["weight"] = ["Lighter", "Default", "Bolder"],
        ["color"] = ["Default", "Dark", "Light", "Accent", "Good", "Warning", "Attention", "default", "dark", "light", "accent", "good", "warning", "attention"],
        ["spacing"] = ["None", "Small", "Default", "Medium", "Large", "ExtraLarge", "Padding"],
        ["horizontalAlignment"] = ["Left", "Center", "Right"],
        ["verticalContentAlignment"] = ["Top", "Center", "Bottom"],
        ["style"] = ["default", "heading"]
    };

    /// <summary>Returns every violation of the AC 1.6 subset the widget may use; empty = valid.</summary>
    public static List<string> ShapeErrors(JsonObject card)
    {
        var errors = new List<string>();
        if ((string?)card["type"] != "AdaptiveCard") errors.Add("root type");
        if ((string?)card["version"] != "1.6") errors.Add("version != 1.6");
        if (card["body"] is not JsonArray body || body.Count == 0) errors.Add("empty body");
        else CheckElements(body, "body", errors);
        CheckAction(card["selectAction"], "selectAction", errors);
        return errors;
    }

    private static void CheckElements(JsonArray elements, string path, List<string> errors)
    {
        for (var i = 0; i < elements.Count; i++)
        {
            var p = $"{path}[{i}]";
            if (elements[i] is not JsonObject e) { errors.Add($"{p}: not an object"); continue; }
            var type = (string?)e["type"];
            if (type is null || !ElementTypes.Contains(type)) { errors.Add($"{p}: type {type}"); continue; }
            CheckEnums(e, p, errors);
            CheckAction(e["selectAction"], p + ".selectAction", errors);
            switch (type)
            {
                case "TextBlock":
                    if (e["text"] is not JsonValue t || !t.TryGetValue<string>(out _)) errors.Add($"{p}: TextBlock.text");
                    break;
                case "RichTextBlock":
                    if (e["inlines"] is not JsonArray inlines || inlines.Count == 0) { errors.Add($"{p}: inlines"); break; }
                    foreach (var run in inlines)
                    {
                        if (run is not JsonObject r || (string?)r["type"] != "TextRun" || r["text"] is not JsonValue)
                            errors.Add($"{p}: inline is not a TextRun");
                        else CheckEnums(r, p + ".run", errors);
                    }
                    break;
                case "Container":
                    CheckBackground(e, p, errors);
                    if (e["items"] is not JsonArray items) errors.Add($"{p}: items");
                    else CheckElements(items, p + ".items", errors);
                    break;
                case "ColumnSet":
                    if (e["columns"] is not JsonArray columns || columns.Count == 0) { errors.Add($"{p}: columns"); break; }
                    for (var c = 0; c < columns.Count; c++)
                    {
                        if (columns[c] is not JsonObject col || (string?)col["type"] != "Column" || col["items"] is not JsonArray colItems)
                        {
                            errors.Add($"{p}.columns[{c}]: Column");
                            continue;
                        }

                        CheckEnums(col, $"{p}.columns[{c}]", errors);
                        CheckWidth(col["width"], $"{p}.columns[{c}]", errors);
                        CheckElements(colItems, $"{p}.columns[{c}].items", errors);
                    }
                    break;
                case "Image":
                    if (e["url"] is not JsonValue url || !url.TryGetValue<string>(out _)) errors.Add($"{p}: Image.url");
                    if (e["altText"] is not JsonValue alt || string.IsNullOrEmpty(alt.GetValue<string>())) errors.Add($"{p}: Image.altText");
                    break;
                case "ActionSet":
                    if (e["actions"] is not JsonArray actions || actions.Count == 0) errors.Add($"{p}: actions");
                    else for (var a = 0; a < actions.Count; a++) CheckAction(actions[a], $"{p}.actions[{a}]", errors);
                    break;
            }
        }
    }

    // Column width: "auto" | "stretch" | a positive weight (number). A weight of 0 is never emitted: the
    // zero-weight column is removed by $when instead.
    private static void CheckWidth(JsonNode? width, string path, List<string> errors)
    {
        switch (width)
        {
            case JsonValue v when v.TryGetValue<string>(out var s) && s is "auto" or "stretch":
                return;
            case JsonValue v when v.TryGetValue<int>(out var w) && w > 0:
                return;
            default:
                errors.Add($"{path}: width={width?.ToJsonString()}");
                return;
        }
    }

    private static void CheckBackground(JsonObject e, string path, List<string> errors)
    {
        if (e["backgroundImage"] is null) return;
        if (e["backgroundImage"] is not JsonObject bg || bg["url"] is not JsonValue || (string?)bg["fillMode"] is not ("repeat" or "cover"))
            errors.Add($"{path}: backgroundImage");
        if (e["minHeight"] is not JsonValue mh || !((string?)mh)!.EndsWith("px", StringComparison.Ordinal))
            errors.Add($"{path}: a background bar needs minHeight in px");
    }

    private static void CheckAction(JsonNode? action, string path, List<string> errors)
    {
        if (action is null) return;
        if (action is not JsonObject a || (string?)a["type"] != "Action.Execute" || a["verb"] is not JsonValue)
            errors.Add($"{path}: only Action.Execute with a verb is allowed");
    }

    private static void CheckEnums(JsonObject e, string path, List<string> errors)
    {
        foreach (var (name, allowed) in Enums)
        {
            if (e[name] is JsonValue v && v.TryGetValue<string>(out var s) && !allowed.Contains(s))
                errors.Add($"{path}: {name}={s}");
        }

        if (e["isSubtle"] is JsonValue subtle && !subtle.TryGetValue<bool>(out _)) errors.Add($"{path}: isSubtle not bool");
        if (e["wrap"] is JsonValue wrap && !wrap.TryGetValue<bool>(out _)) errors.Add($"{path}: wrap not bool");
    }

    // ---- walkers -----------------------------------------------------------------------------------------

    /// <summary>All objects in the tree, with their parent-array owner key ("inlines", "items", …).</summary>
    public static IEnumerable<(JsonObject Node, string? OwnerKey)> Objects(JsonNode? node, string? ownerKey = null)
    {
        switch (node)
        {
            case JsonObject obj:
                yield return (obj, ownerKey);
                foreach (var (name, child) in obj)
                {
                    foreach (var inner in Objects(child, name))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var inner in Objects(item, ownerKey))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }

    /// <summary>Every (key, string value) pair in the tree.</summary>
    public static IEnumerable<(string Key, string Value)> Strings(JsonNode? node)
    {
        foreach (var (obj, _) in Objects(node))
        {
            foreach (var (name, child) in obj)
            {
                if (child is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    yield return (name, s);
                }
            }
        }
    }

    /// <summary>Every Action.* object anywhere in the card.</summary>
    public static List<JsonObject> Actions(JsonNode card) =>
        Objects(card).Select(o => o.Node)
            .Where(o => ((string?)o["type"])?.StartsWith("Action.", StringComparison.Ordinal) == true)
            .ToList();

    /// <summary>The expanded server-row containers (those with an openServer selectAction).</summary>
    public static List<JsonObject> Rows(JsonNode card) =>
        Objects(card).Select(o => o.Node)
            .Where(o => o["selectAction"] is JsonObject a && (string?)a["verb"] == "openServer")
            .ToList();

    /// <summary>All visible text of an element: TextBlock texts and TextRun texts.</summary>
    public static List<string> VisibleTexts(JsonNode? node) =>
        Objects(node).Select(o => o.Node)
            .Where(o => (string?)o["type"] is "TextBlock" or "TextRun")
            .Select(o => (string?)o["text"] ?? string.Empty)
            .ToList();
}
