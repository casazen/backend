# Code Review Guidelines for CasaZen

> **Purpose**: Review-specific rules for Claude Code automated reviews
>
> **Note**: General project guidelines are in `CLAUDE.md` and `.claude/rules/`. This file contains **review-only** rules that Claude should enforce during PR reviews.

---

## ✅ Always Check

### Security & Critical Issues
- **Authentication & Authorization**: Every API endpoint has proper [Authorize] attributes (unless explicitly public)
- **Input Validation**: All user inputs are validated using data annotations and model validation
- **SQL Injection**: No string concatenation in queries (use EF Core or parameterized queries only)
- **Secrets Management**: No hardcoded credentials, API keys, or connection strings
- **Error Exposure**: Error messages don't leak internal implementation details to external APIs
- **Webhook Security**: Stripe webhooks verify signatures (prevent spoofing)

### Italian Regulatory Compliance
- **CIN Codes**: Format validation (IT-XXXXX-XXXXXXXXXX) for all property-related features
- **GDPR**: Guest data handling follows retention policies and data protection rules
- **Tourist Tax**: No hardcoded rates (must use database TaxRate entity)
- **Alloggiati Web**: Guest reporting integration follows Italian regulations

### Async & Database
- **Async Methods**: All I/O operations (database, HTTP, file) use async/await
- **Method Naming**: Async methods end with "Async" suffix (`GetUserAsync`, `SaveBookingAsync`)
- **Deadlock Prevention**: No `.Result` or `.Wait()` calls (causes deadlocks)
- **Database Migrations**: Any schema change has a corresponding EF Core migration
- **DbContext Usage**: DbContext is scoped per request (never stored in static fields)
- **UTC Timestamps**: All date/time handling uses UTC internally (`DateTime.UtcNow`)

### Testing Requirements
- **New Features**: All new features have corresponding tests
- **Test Coverage**: Critical logic 100%, Services 80%, Controllers 70%
- **Test Naming**: `MethodName_Scenario_ExpectedBehavior` pattern
- **AAA Pattern**: Tests follow Arrange-Act-Assert structure
- **Mocking**: Use Moq for dependencies, not manual mocks

### API Integrations
- **HTTPS**: All external API calls use HTTPS (Auth0, Stripe, SendGrid)
- **OTA Timeouts**: Webhook responses within 3 seconds (use background jobs for long operations)
- **Rate Limits**: OTA adapter calls respect platform rate limits (exponential backoff retry)
- **Error Handling**: Log errors with context using ILogger (include relevant IDs)

---

## 🎨 Style & Conventions

### Architecture
- **Repository Pattern**: All data access via IRepository interfaces (no direct DbContext in controllers)
- **Dependency Injection**: Services registered in Program.cs (constructor injection everywhere)
- **Layer Separation**: Controllers → Services → Repositories → DbContext (respect layer boundaries)
- **OTA Adapters**: Each platform has dedicated adapter implementing IOtaAdapter

### Code Quality
- **SOLID Principles**: Single responsibility, interface segregation, dependency inversion
- **Early Returns**: Prefer early returns over nested conditionals
- **Magic Numbers**: No hardcoded values (use constants or configuration)
- **Null Handling**: Use null-conditional operators (`?.`, `??`) where appropriate
- **Exception Handling**: Catch specific exceptions, not generic `catch (Exception)`

### .NET Conventions
- **PascalCase**: Classes, methods, properties, public fields
- **camelCase**: Private fields (prefix with `_`), local variables, parameters
- **Interfaces**: Prefix with `I` (e.g., `IPropertyRepository`)
- **Async Suffix**: All async methods end with "Async"
- **Dispose Pattern**: Use `using` statements or implement IDisposable correctly

### Git & Commits
- **Conventional Commits**: Use `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`
- **Commit Messages**: Clear, concise, in English (no AI attribution)
- **Branch Naming**: `feature/`, `fix/`, `hotfix/` prefixes
- **PR Requirements**: Link to issue (`Closes #123`), pass all CI checks, include tests

---

## 🚫 Skip / Ignore

### Don't Comment On
- **Formatting**: Already handled by `dotnet format` (don't flag spacing, indentation)
- **Generated Files**: Files in `/obj/`, `/bin/`, migrations auto-generated code
- **Lock Files**: `packages.lock.json`, `*.lock` files (formatting-only changes)
- **Third-Party Code**: NuGet packages, external libraries
- **TODO Comments**: Unless they're in production-critical code paths

### Known Patterns (Don't Flag)
- **EF Core Navigation Properties**: Circular references in entities (normal for EF Core)
- **Constructor Injection**: Large constructor parameter lists (DI pattern)
- **appsettings.json**: Missing sensitive values (stored in appsettings.Development.json locally)
- **Test Mocks**: Verbose mock setups in test files (necessary for isolation)

---

## 📊 Severity Guidelines

### 🔴 Critical (Must Fix Before Merge)
- Security vulnerabilities (SQL injection, XSS, auth bypass)
- Secrets committed to repository
- Breaking changes without migration
- `.Result` or `.Wait()` usage (deadlock risk)
- Missing input validation on public APIs
- Regulatory compliance violations (CIN, GDPR, tourist tax)

### 🟡 High (Should Fix Before Merge)
- Missing tests for new features
- Async method without "Async" suffix
- Violation of SOLID principles
- Poor error handling (generic catches, swallowed exceptions)
- Hardcoded configuration values
- Missing XML documentation on public APIs

### 🟢 Medium (Fix If Time Permits)
- Code duplication (consider refactoring)
- Overly complex methods (high cyclomatic complexity)
- Magic numbers (should be constants)
- Missing logging in important operations
- TODO comments without issue references

### ⚪ Low (Nit / Suggestions)
- Style inconsistencies not caught by formatter
- Variable naming improvements
- Performance micro-optimizations
- Additional test scenarios
- Suggested refactoring for clarity

---

## 🤖 Review Process

Claude Code will:
1. ✅ Analyze PR diff against these guidelines
2. ✅ Check surrounding code context (not just diff)
3. ✅ Post inline comments on specific lines
4. ✅ Tag findings by severity (🔴 Critical, 🟡 High, 🟢 Medium, ⚪ Low)
5. ✅ Verify issues against actual code behavior (reduce false positives)
6. ✅ Provide extended reasoning for each finding

**Expected Outcome**: Zero critical issues before merge, high/medium issues addressed or documented, low issues optional.

---

**Last Updated**: 2026-03-31
**Maintained By**: CasaZen Development Team
**Related**: @CLAUDE.md | @.claude/rules/ | [GitHub Actions](https://code.claude.com/docs/en/github-actions)
