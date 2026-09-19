using System;
using System.Text.Json;
using System.Collections.Generic;
using System.IO;
using AiAssistant.Core.Services;
using AiAssistant.Core.Models;
using AiAssistant.Llm.Services;
using AiAssistant.Llm.Models;
using AiAssistant.Engine.Services;
using AiAssistant.Engine.Models;
using AiAssistant.Engine.SkeletonEngine;

class Program
{
    static void Main()
    {
        Console.WriteLine("--- PHASE 1 HEADLESS SIMULATION ---");
        SimulateParserRouterOmission();
        SimulateParserRouterTruncation();
    }

    static void SimulateParserRouterOmission()
    {
        // Test 1: Fallback D minified omission
        string content = new string('a', 60000); // long line
        var result = ParserRouter.Parse("main.min.js", content, 10000);
        Console.WriteLine("\n[Simulation 1: Minified File Omission]");
        Console.WriteLine($"StatusKind: {result.StatusKind}");
        Console.WriteLine($"StatusDetail: {result.StatusDetail}");
    }

    static void SimulateParserRouterTruncation()
    {
        // Test 2: Fallback B native truncation on .cs file
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("class BigClass {");
        for (int i = 1; i <= 2000; i++)
        {
            sb.AppendLine($"    public void Method{i}() {{ }}");
        }
        sb.AppendLine("}");
        string content = sb.ToString();

        // Pass a max budget of 1500 chars (which is way too small for 2000 lines)
        var result = ParserRouter.Parse("BigFile.cs", content, 1500);
        
        Console.WriteLine("\n[Simulation 2: Native Truncation (.cs)]");
        Console.WriteLine($"StatusKind: {result.StatusKind}");
        Console.WriteLine($"StatusDetail: {result.StatusDetail}");
        Console.WriteLine("Outline output preview:");
        Console.WriteLine("```");
        Console.WriteLine(result.Outline.Length > 400 ? result.Outline.Substring(0, 400) + "\n...[SNIP]..." : result.Outline);
        Console.WriteLine("```");
        Console.WriteLine($"Outline length: {result.Outline.Length}");
        Console.WriteLine($"Included TRUNCATED definition block? {result.Outline.Contains("TRUNCATED")}");
    }
}
