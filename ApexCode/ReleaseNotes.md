## 1.1 — Initial Release

### Added
- Multi-provider AI chat with BYOK support (OpenAI, Azure OpenAI, OpenRouter,
  DeepSeek, Gemini, NVIDIA, Vercel)
- Plan and Act agent modes with tool-level read-only guarding
- Paginated large-file reading with 10MB hard cap and write-time caching
- Safe SQL Server integration: schema inspection and read-only query execution
  (writes and stored procedures blocked, 100-row result cap)
- Roslyn-powered diagnostics, compile validation, and in-place code fixes
- Automatic checkpointing with visual diff review and one-click revert
- Custom persona / system prompt support
- Dynamic token budget management with automatic history eviction
- Enhanced Plan Review window with task and question tracking
- Support for Visual Studio 2022 and Visual Studio 2026
