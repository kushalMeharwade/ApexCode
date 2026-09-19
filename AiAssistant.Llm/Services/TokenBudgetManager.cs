using System;
using System.Collections.Generic;
using System.Linq;
using AiAssistant.Llm.Models;
using Microsoft.ML.Tokenizers;

namespace AiAssistant.Llm.Services
{
    public class TokenBudgetManager
    {
        private readonly Tokenizer _tokenizer;

        public TokenBudgetManager()
        {
            // For this phase, we use cl100k_base which is standard for GPT-4/3.5
            // In a real scenario, this might be injected based on the selected model.
            _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");
        }

        public int CountTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return _tokenizer.CountTokens(text);
        }

        public PromptContext TrimToBudget(PromptContext original, int maxTokens)
        {
            var result = new PromptContext
            {
                UserRequest = original.UserRequest,
                TargetFramework = original.TargetFramework,
                SelectedText = original.SelectedText,
                CursorLine = original.CursorLine
            };

            int tier1Limit = (int)(maxTokens * 0.50);
            int tier2Limit = (int)(maxTokens * 0.35);
            int tier3Limit = (int)(maxTokens * 0.15);

            // TIER 1: UserRequest + ActiveFileContent + SelectedText
            int baseTokens = CountTokens(result.UserRequest) + CountTokens(result.SelectedText);
            
            if (baseTokens > tier1Limit)
            {
                throw new InvalidOperationException("User request and selected text exceed the entire Tier 1 budget.");
            }

            int remainingTier1 = tier1Limit - baseTokens;
            result.ActiveFileContent = TrimStringEnd(original.ActiveFileContent, remainingTier1, out int usedActive);
            remainingTier1 -= usedActive;

            // Spill unused Tier 1 to Tier 2
            tier2Limit += remainingTier1;

            // TIER 2: ContextFiles
            int tier2Used = 0;
            foreach (var file in original.ContextFiles)
            {
                int needed = CountTokens(file.FilePath) + CountTokens(file.Content) + 10; // 10 tokens for formatting
                if (tier2Used + needed <= tier2Limit)
                {
                    result.ContextFiles.Add(new ContextFile { FilePath = file.FilePath, Content = file.Content });
                    tier2Used += needed;
                }
                else
                {
                    // Partial trim of context file
                    int canUse = tier2Limit - tier2Used;
                    string trimmedContent = TrimStringEnd(file.Content, canUse - CountTokens(file.FilePath) - 10, out int actuallyUsed);
                    
                    if (!string.IsNullOrEmpty(trimmedContent))
                    {
                        result.ContextFiles.Add(new ContextFile { FilePath = file.FilePath, Content = trimmedContent });
                        tier2Used += (CountTokens(file.FilePath) + 10 + actuallyUsed);
                    }
                    break; // Budget exhausted
                }
            }

            int remainingTier2 = tier2Limit - tier2Used;
            tier3Limit += remainingTier2;

            // TIER 3: ExistingErrors, NuGetPackages
            int tier3Used = 0;
            
            foreach (var error in original.ExistingErrors)
            {
                int needed = CountTokens(error) + 2;
                if (tier3Used + needed <= tier3Limit)
                {
                    result.ExistingErrors.Add(error);
                    tier3Used += needed;
                }
                else
                {
                    break;
                }
            }

            foreach (var pkg in original.NuGetPackages)
            {
                int needed = CountTokens(pkg) + 2;
                if (tier3Used + needed <= tier3Limit)
                {
                    result.NuGetPackages.Add(pkg);
                    tier3Used += needed;
                }
                else
                {
                    break;
                }
            }

            return result;
        }

        private string TrimStringEnd(string input, int maxTokens, out int usedTokens)
        {
            if (string.IsNullOrEmpty(input))
            {
                usedTokens = 0;
                return "";
            }

            var tokens = _tokenizer.EncodeToIds(input);
            if (tokens.Count <= maxTokens)
            {
                usedTokens = tokens.Count;
                return input;
            }

            if (maxTokens <= 0)
            {
                usedTokens = 0;
                return "";
            }

            var trimmedTokens = tokens.Take(maxTokens).ToList();
            usedTokens = trimmedTokens.Count;
            return _tokenizer.Decode(trimmedTokens);
        }
    }
}
