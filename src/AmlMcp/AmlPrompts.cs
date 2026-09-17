using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AmlMcp;

/// <summary>Prepared questions for the tasks this server is built for.</summary>
[McpServerPromptType]
public static class AmlPrompts
{
    [McpServerPrompt(Name = "explain_document")]
    [Description("Walk through an AutomationML document and explain what it contains.")]
    public static string ExplainDocument(
        [Description("Path of the .aml or .amlx file.")] string path) =>
        $"""
        Open the AutomationML document {path} with the aml tools and explain it to an engineer
        who has not seen it before. Cover: which views or hierarchies it contains and what each
        one describes, which class libraries it builds on, and how the hierarchies are connected
        to each other. Use get_tree and get_element for the parts that matter, name element IDs
        so every statement can be checked, and say plainly what the document does not contain.
        """;

    [McpServerPrompt(Name = "trace_to_plant")]
    [Description("Trace an element to the plant structure and to the other views of the same plant.")]
    public static string TraceToPlant(
        [Description("Element ID, path or unique name to start from.")] string element) =>
        $"""
        Starting at {element}, follow the connections of the open AutomationML document to the
        plant structure and from there into the other views. Use get_element and get_neighbors,
        and find_path when you look for a specific target. Report the chain of elements with
        their IDs, say which mechanism carries each step (InternalLink, reference attribute or
        mirror), and point out where the chain ends.
        """;

    [McpServerPrompt(Name = "review_document")]
    [Description("Check an AutomationML document for referential problems and summarise them.")]
    public static string ReviewDocument(
        [Description("Path of the .aml or .amlx file.")] string path) =>
        $"""
        Open {path} and run check_references. Explain each finding in a sentence an engineer can
        act on: what is broken, where, and what the likely cause is. Distinguish real problems
        from notes. If a library could not be loaded, say which classes depend on it. Do not
        repeat the raw output, summarise it.
        """;
}
