# ADR-035: Multilingual Content Storage

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-31 (14 languages)

## Context

Octorate supports 14 languages for listing content. Hosts need IT and EN manually, with AI translation assistance for other 12 languages. Per-channel content variants are deferred (P2).

**Existing code:**
- No multilingual content infrastructure exists
- AI translation placeholder exists

**Repo scope:** Backend (translation storage, AI integration), Frontend (multilingual editor), Mobile (none - listing editing is desktop)

## Decision

### Translation Storage

Per Q-A16: one version per language for all channels.

```csharp
public class ListingTranslation : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid PropertyId { get; set; }
    public string LanguageCode { get; set; }        // ISO 639-1: it, en, fr, de, ...
    public string FieldName { get; set; }           // name, description, houseRules
    public string TranslatedText { get; set; }
    public bool IsAutoTranslated { get; set; }      // true = AI, false = user
    public DateTime LastModifiedAt { get; set; }
}
```

Unique index on `(PropertyId, LanguageCode, FieldName)`.

### Supported Languages

Per Octorate spec (14 languages):

| Code | Language | Mandatory |
|------|----------|-----------|
| it | Italian | Yes |
| en | English | Yes |
| fr | French | No |
| de | German | No |
| es | Spanish | No |
| ru | Russian | No |
| pt | Portuguese | No |
| nl | Dutch | No |
| ja | Japanese | No |
| el | Greek | No |
| tr | Turkish | No |
| zh | Chinese | No |
| ca | Catalan | No |
| ro | Romanian | No |

### AI Translation Flow

Per Q-A15: manual IT/EN mandatory, AI for other 12.

```csharp
public async Task TranslateToAllAsync(Guid propertyId, string fieldName, CancellationToken ct)
{
    var sourceText = await _db.ListingTranslations
        .Where(t => t.PropertyId == propertyId && t.FieldName == fieldName && t.LanguageCode == "en")
        .Select(t => t.TranslatedText)
        .SingleOrDefaultAsync(ct);
        
    if (string.IsNullOrEmpty(sourceText))
        throw new ValidationException("translation.englishRequired");
        
    var targetLanguages = new[] { "fr", "de", "es", "ru", "pt", "nl", "ja", "el", "tr", "zh", "ca", "ro" };
    
    foreach (var lang in targetLanguages)
    {
        var existing = await _db.ListingTranslations
            .Where(t => t.PropertyId == propertyId && t.FieldName == fieldName && t.LanguageCode == lang)
            .SingleOrDefaultAsync(ct);
            
        // Never overwrite user edits
        if (existing != null && !existing.IsAutoTranslated)
            continue;
            
        var translated = await _aiService.TranslateAsync(sourceText, "en", lang, ct);
        
        if (existing != null)
        {
            existing.TranslatedText = translated;
            existing.IsAutoTranslated = true;
            existing.LastModifiedAt = DateTime.UtcNow;
        }
        else
        {
            _db.ListingTranslations.Add(new ListingTranslation
            {
                PropertyId = propertyId,
                LanguageCode = lang,
                FieldName = fieldName,
                TranslatedText = translated,
                IsAutoTranslated = true
            });
        }
    }
    
    await _db.SaveChangesAsync(ct);
}
```

### Multilingual Editor UI

```typescript
function MultilingualEditor({ propertyId, fieldName }) {
    return (
        <div>
            {/* Mandatory tabs */}
            <Tabs defaultValue="it">
                <TabsList>
                    <TabsTrigger value="it">Italiano *</TabsTrigger>
                    <TabsTrigger value="en">English *</TabsTrigger>
                </TabsList>
                <TabsContent value="it">
                    <TranslationField language="it" mandatory />
                </TabsContent>
                <TabsContent value="en">
                    <TranslationField language="en" mandatory />
                </TabsContent>
            </Tabs>
            
            {/* Optional accordion */}
            <Accordion type="single" collapsible>
                <AccordionItem value="other">
                    <AccordionTrigger>{t('translation.otherLanguages')}</AccordionTrigger>
                    <AccordionContent>
                        {OTHER_LANGUAGES.map(lang => (
                            <TranslationField 
                                key={lang} 
                                language={lang} 
                                showAutoIndicator 
                            />
                        ))}
                    </AccordionContent>
                </AccordionItem>
            </Accordion>
            
            <Button onClick={translateToAll}>{t('translation.translateToAll')}</Button>
        </div>
    );
}
```

### User Edit Protection

User edit of AI translation sets `IsAutoTranslated = false`:

```csharp
public async Task SaveTranslationAsync(SaveTranslationDto dto, CancellationToken ct)
{
    var existing = await _db.ListingTranslations
        .Where(t => t.PropertyId == dto.PropertyId && t.LanguageCode == dto.Language && t.FieldName == dto.FieldName)
        .SingleOrDefaultAsync(ct);
        
    if (existing != null)
    {
        existing.TranslatedText = dto.Text;
        existing.IsAutoTranslated = false;  // User edited = no longer auto
        existing.LastModifiedAt = DateTime.UtcNow;
    }
    else
    {
        _db.ListingTranslations.Add(new ListingTranslation { ... });
    }
    
    await _db.SaveChangesAsync(ct);
}
```

### Rejected Alternatives

- **Per-channel variants:** Deferred to P2; single version per language is simpler
- **All 14 languages mandatory:** Too burdensome; only IT/EN required
- **Auto-overwrite on re-translate:** Would lose user edits; never overwrite `IsAutoTranslated = false`

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-035-R1 | P0 | `ListingTranslation` table (OrgId, PropertyId, LanguageCode varchar(2), FieldName, TranslatedText, IsAutoTranslated bool, LastModifiedAt, ITenantOwned) with unique index (PropertyId, LanguageCode, FieldName) |
| ADR-035-R2 | P0 | Save validation MUST enforce IT and EN non-empty before allowing listing publish; other languages optional |
| ADR-035-R3 | P0 | AI-translated content MUST set `IsAutoTranslated = true`; user edit MUST set `IsAutoTranslated = false` |
| ADR-035-R4 | P0 | "Translate to all" MUST NOT overwrite fields where `IsAutoTranslated = false` (user edits protected) |
| ADR-035-R5 | P0 | Multilingual editor MUST show tabs for IT and EN (mandatory), accordion for other 12 languages |
| ADR-035-R6 | P0 | Auto-translated fields MUST show visual indicator (badge/icon) distinguishing from user-entered |
| ADR-035-R7 | P1 | "Translate to all" button MUST show loading state, translate all 12 optional languages from EN source |
| ADR-035-R8 | P1 | i18n keys MUST exist: `translation.autoTranslated`, `translation.translateToAll`, `translation.otherLanguages` (IT/EN) |

## Consequences

**Follow-on work:**
- Backend: `ListingTranslation` entity, AI translation service integration, save endpoint
- Frontend: Multilingual editor with tabs and accordion, auto-translated indicator
- Mobile: None (listing editing is desktop)
- Tests: User edit protection integration test

**Tier 2 priority:** This is a Tier 2 ADR. May be deferred if wave 1 timeline is tight.

**Risks:**
- AI translation cost: ~$600 per batch (60k calls at $0.01/call); budget approval needed
- Translation quality: AI translations need user review; indicate with badge

**CI cost:** +5s for user edit protection test
