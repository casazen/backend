# CasaZen Project Configuration

This directory contains project-specific configuration that global agent templates read to adapt their behavior.

## Files

### project.json
Main project configuration containing:
- **Project metadata**: Name, description, domain, version
- **Tech stack**: Frameworks, languages, tools, testing setup
- **Architecture**: Layers, patterns, project structure
- **Coding conventions**: Style guide, naming, formatting, principles
- **Testing**: Frameworks, coverage targets, test commands
- **Database**: Provider, migrations, conventions
- **API**: REST conventions, authentication, documentation
- **External integrations**: Auth0, Stripe, SendGrid, OTA platforms
- **Compliance**: Regulatory context and requirements
- **Git workflow**: Branching, commits, deployment
- **Documentation**: Locations of key docs

## How Agents Use This Config

### Global Agents (from ~/.claude/agents/)
Read `project.json` to understand:
1. **What tech stack to use**: .NET, SQL Server, xUnit
2. **What conventions to follow**: Microsoft C# conventions, PascalCase, async/await
3. **Where files are located**: Controllers in Casazen.Web/Controllers/, entities in Casazen.Core/Entities/
4. **How to test**: `dotnet test`, xUnit framework, 75% coverage target
5. **Domain context**: Vacation rentals, Italian compliance, OTA integrations

### Example: Feature Developer Agent
When implementing a feature, the agent:
```
1. Reads project.json
2. Sees tech_stack.framework = ".NET 10"
3. Sees coding_conventions.naming.methods = "PascalCase"
4. Sees project_structure.entities = "Casazen.Core/Entities/"
5. Implements feature following these conventions
```

## When to Update

Update `project.json` when:
- ✅ Tech stack changes (new framework version, new libraries)
- ✅ Coding conventions evolve (new patterns adopted)
- ✅ Project structure changes (new directories, layers)
- ✅ Testing approach changes (new framework, different coverage targets)
- ✅ New external integrations added
- ✅ Compliance requirements change

## CasaZen-Specific Context

### Domain: Vacation Rental Management
The project operates in the Italian short-term rental market with:
- Multiple OTA platform integrations (Airbnb, Booking.com, etc.)
- Strict regulatory compliance requirements (CIN codes, guest reporting, tourist tax)
- Payment processing via Stripe
- Guest communication via SendGrid

### Architecture: Layered
```
Casazen.Web (Presentation)
    ↓
Casazen.Core (Business Logic)
    ↓
Casazen.Infrastructure (Data + External Services)
    ↓
SQL Server (Database)
```

### Key Compliance Requirements
- **CIN Code Management**: D.L. 145/2023 compliance
- **Alloggiati Web**: Police guest reporting
- **Tourist Tax**: Regional variations
- **GDPR**: Data protection
- **DAC7**: Platform reporting

### Regulatory Intelligence System
The project has automated agents that:
1. Monitor regulatory updates (regulatory_agent)
2. Analyze compliance gaps (analyzer_agent)
3. Create GitHub issues (github_agent)

These are **domain-specific** and live in `.claude/agents/` (not global templates).

## Integration with Global System

```
~/.claude/
├── agents/              # Global templates (architect, developer, tester, etc.)
├── skills/              # Global skills (write_user_story, create_pr, etc.)
└── ...

casazen/backend/
├── .claude/
│   ├── config/
│   │   └── project.json         # THIS FILE - Project-specific config
│   ├── agents/
│   │   ├── regulatory_agent.md  # Domain-specific (Italian regulations)
│   │   ├── analyzer_agent.md    # Domain-specific (compliance analysis)
│   │   └── github_agent.md      # Domain-specific (regulatory issues)
│   ├── skills/
│   │   ├── classify_topic.md    # Domain-specific (regulatory topics)
│   │   └── diff_context.md      # Domain-specific (regulatory context)
│   └── context/                 # Regulatory knowledge base
│       ├── domain.md
│       ├── codebase_map.md
│       ├── _index.md
│       └── regulations/
└── Casazen.sln
```

### How It Works
1. **Global agents** (e.g., feature_developer) read `project.json` to understand CasaZen specifics
2. **Domain-specific agents** (e.g., regulatory_agent) handle compliance workflows
3. **Both types** use **global skills** (write_user_story, create_pr) and **domain skills** (classify_topic)
4. **Result**: Generic development workflow + Domain-specific compliance automation

## Best Practices

### Keep Config Updated
When the project evolves, update `project.json` so agents stay aligned.

### Document Decisions
Use comments in JSON (not supported natively, so document in README) to explain:
- Why certain conventions were chosen
- What architectural decisions were made
- What constraints exist (regulatory, technical, business)

### Version Control
**Commit** `project.json` to the repository so:
- Team members benefit from agent configuration
- Configuration is versioned alongside code
- New developers onboard faster

### Privacy
**Don't include** in `project.json`:
- API keys or secrets
- Database connection strings
- Credentials or tokens
- Personal or sensitive data

Store secrets in:
- `appsettings.Development.json` (gitignored)
- Environment variables
- Secret management services (Azure Key Vault, etc.)

## Example: Using Agents with This Config

### Scenario: Implement CIN Code Feature

```bash
# 1. Product Owner creates user story
# Reads project.json to understand domain (vacation rentals, compliance)
# Outputs: Well-structured user story for CIN management

# 2. Architect designs solution
# Reads project.json to understand architecture (layered, EF Core, repositories)
# Outputs: Technical spec with entities, services, migrations

# 3. Issue Planner breaks down implementation
# Reads project.json to know where files go (entities in Core, repos in Infrastructure)
# Outputs: Task list with specific file paths

# 4. Feature Developer implements
# Reads project.json to follow conventions (PascalCase, async/await, DI)
# Outputs: Code in Casazen.Core/Entities/Property.cs with CinCode field

# 5. Test Engineer creates tests
# Reads project.json to know testing framework (xUnit) and coverage targets (75%)
# Outputs: Tests in Casazen.Tests/Unit/PropertyTests.cs

# 6. Code Reviewer checks quality
# Reads project.json to verify conventions followed
# Outputs: Approval or feedback

# 7. Release Manager creates PR
# Reads project.json to format PR correctly (no AI co-authorship per user preference)
# Outputs: PR with proper structure
```

All agents work together seamlessly because they share the same project understanding via `project.json`.

## Support

Questions about configuration?
- Review `project.json` comments
- Check global agent documentation in `~/.claude/agents/README.md`
- Consult `CLAUDE.md` for project-specific guidelines
