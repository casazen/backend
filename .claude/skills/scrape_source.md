# Skill: Scrape Source - Scraping Institutional Sources

## Description
This skill describes how to scrape Italian and European institutional sources to extract relevant regulatory information using Claude Code tools.

## When to Use It
- When you need to retrieve the text of a law, decree, or regulation
- When you need to check updates on institutional websites
- When you need to extract structured content from regulatory web pages

## Supported Sources

### Italian Sources
| Source | Base URL | Notes |
|--------|----------|-------|
| Official Gazette | gazzettaufficiale.it | Official legal texts |
| Revenue Agency | agenziaentrate.gov.it | Circulars, rulings, guides |
| Ministry of Tourism | ministeroturismo.gov.it | Tourism regulations, CIN |
| Normattiva | normattiva.it | Consolidated legal texts |
| BDSR | bdsr.ministeroturismo.it | Accommodation Facilities Database |

### European Sources
| Source | Base URL | Notes |
|--------|----------|-------|
| EUR-Lex | eur-lex.europa.eu | EU directives and regulations |
| European Commission | ec.europa.eu | Proposals and communications |

## Procedure

### Step 1: Search

    WebSearch("search term site:domain.gov.it")

Use specific terms and restrict the query to the institutional domain.

### Step 2: Fetch

    WebFetch(url, "Extract the main regulatory text, including: full title, number and date of the act, relevant articles, effective dates, and any applicable penalties")

### Step 3: Validation
Verify that the extracted content includes:
- [x] Full regulatory reference (e.g., "D.L. 145/2023, art. 13-ter")
- [x] Publication/effective date
- [x] Relevant article text
- [x] Any subsequent amendments

### Step 4: Structuring
Organize the extracted content in the following format:

    # [Title of the Act]

    - **Type**: Decree Law / Law / EU Directive / Circular
    - **Number**: [number/year]
    - **Publication date**: [date]
    - **Effective date**: [date]
    - **Source**: [URL]
    - **Consultation date**: [current date]

    ## Summary
    [brief summary of the obligation]

    ## Relevant Articles
    ### Art. [N] - [Title]
    [text or summary of the article]

    ## Impact on CasaZen
    [description of how this regulation impacts the system]

    ## Penalties
    [any penalties for non-compliance]

## Error Handling
- If the page is not reachable: note it in the report and try an alternative source
- If the content is a PDF: use `WebFetch` with a specific prompt to extract the text
- If the content is too long: request extraction of only relevant sections (e.g., short-term rentals)

## Best Practices
- Always prefer `normattiva.it` for consolidated legal texts (includes amendments)
- For Revenue Agency circulars, also check related FAQs
- Always store the consultation date for traceability
- Do not rely on non-institutional sources for the exact legal text  