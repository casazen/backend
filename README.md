# backend# Contributing to CASAZEN

## 🤝 How to Contribute

### Code Style
- Use C# conventions (PascalCase for classes, camelCase for variables)
- Use async/await for all I/O operations
- Include XML documentation for public members

### Commits
- Use conventional commits: \`feat:\`, \`fix:\`, \`docs:\`, \`refactor:\`
- Example: \`feat: add OTA sync for Airbnb\`

### Pull Request Process
1. Fork the repository
2. Create feature branch: \`git checkout -b feature/amazing-feature\`
3. Commit changes: \`git commit -m 'feat: add amazing feature'\`
4. Push to branch: \`git push origin feature/amazing-feature\`
5. Open Pull Request

### Testing
- Write unit tests for new features
- Maintain >80% code coverage
- Run tests before submitting PR: \`dotnet test\`

### Code Review
- At least 2 approvals required
- All CI/CD checks must pass
- No merge conflicts

## 📋 Reporting Issues
- Use GitHub Issues
- Include: description, steps to reproduce, expected behavior, actual behavior
- Add labels: \`bug\`, \`enhancement\`, \`documentation\`

## 📚 Documentation
- Update README if adding new features
- Include code examples
- Update API documentation in Swagger

---

## 🏗️ Architecture Guidelines

### Layered Architecture
- **Presentation**: Controllers, DTOs
- **Business Logic**: Services, Interfaces
- **Data Access**: Repositories, DbContext
- **Infrastructure**: External services, Adapters

### Naming Conventions
- Repositories: \`IXyzRepository\`, \`XyzRepository\`
- Services: \`IXyzService\`, \`XyzService\`
- Controllers: \`XyzController\`
- Entities: Singular, PascalCase

### Async/Await
- All I/O operations must be async
- Use \`async Task\` for void-returning methods
- Never use \`.Result\` or \`.Wait()\`

---

## 🧪 Testing Standards

### Unit Tests
- Test one thing per test
- Use Arrange-Act-Assert pattern
- Mock external dependencies

### Integration Tests
- Test API endpoints
- Use test database
- Clean up after tests

### Test Naming
- \`MethodName_Scenario_ExpectedResult\`
- Example: \`CreateBooking_WithValidData_ReturnsBooking\`

---

## 📝 Commit Message Format

\`\`\`
<type>(<scope>): <subject>

<body>

<footer>
\`\`\`

### Type
- feat: New feature
- fix: Bug fix
- refactor: Code refactoring
- docs: Documentation
- test: Tests
- chore: Build process

### Scope
- properties, bookings, payments, ota, auth, etc.

### Subject
- Imperative, present tense
- Don't capitalize
- No period at end

### Body
- Motivation for change
- Contrast with previous behavior

### Footer
- Breaking changes
- Issue references: \`Fixes #123\`

---

## 🔄 Release Process

1. Update version in \`.csproj\` files
2. Update CHANGELOG.md
3. Create annotated git tag: \`git tag -a v1.0.0 -m "Release v1.0.0"\`
4. Push tag: \`git push origin v1.0.0\`
5. GitHub Actions automatically builds and deploys

---

## 📞 Questions?
- Open a Discussion on GitHub
- Email: dev@casazen.app
- Join Discord: [Link]

---

Happy coding! 🚀
