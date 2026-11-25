#!/usr/bin/env python3
"""Collect public Quokka listing evidence for the staff import of already-created CasaZen listings."""
import argparse
import csv
from html.parser import HTMLParser
import json
import re
import sys
import time
from pathlib import Path
from urllib.parse import urlencode, urlsplit
from urllib.request import Request, urlopen
from xml.etree import ElementTree

ORIGIN = "https://quokka.kross.travel"
RATE = re.compile(r"^selectRateRadio\((.*?)\)\s*;?$")
UNCONFIRMED_BEDS = {"davos-lodge", "loft-torre", "prestige-du-lac-07", "white-studio-8"}
VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"}


class Listing(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.depth = 0
        self.description_depth = None
        self.rate_depth = None
        self.rate_text_depth = None
        self.rate_text = []
        self.ld_depth = None
        self.description = []
        self.rates = []
        self.documents = []
        self.ld_buffer = []

    def handle_starttag(self, tag, attrs):
        fields = dict(attrs)
        if tag not in VOID:
            self.depth += 1
        if fields.get("id") == "description-apartment-p":
            self.description_depth = self.depth
        if self.description_depth is not None and tag in {"br", "p", "h4", "li"}:
            self.description.append("\n")
        if tag == "script" and fields.get("type") == "application/ld+json":
            self.ld_depth = self.depth
            self.ld_buffer = []
        if (match := RATE.match(fields.get("onclick") or "")) and (fields.get("id") or "").startswith("box-rate"):
            parts = next(csv.reader([match[1]], quotechar="'", skipinitialspace=True))
            if len(parts) >= 6:
                self.rates.append({"id": parts[0], "name": parts[1], "amount": float(parts[3]),
                                   "advance": parts[4], "percent": parts[5], "conditions": None})
                self.rate_depth = self.depth
        if self.rate_depth is not None and fields.get("data-content"):
            self.rates[-1]["conditions"] = fields["data-content"]
        if self.rate_depth is not None and "rate_small" in (fields.get("class") or "").split():
            self.rate_text_depth = self.depth
            self.rate_text = []
        if self.rate_text_depth is not None and tag == "br":
            self.rate_text.append("\n")

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)
        if tag not in VOID:
            self.handle_endtag(tag)

    def handle_endtag(self, tag):
        if tag in VOID:
            return
        if self.depth == self.description_depth:
            self.description_depth = None
        elif self.description_depth is not None and tag in {"p", "h4", "li"}:
            self.description.append("\n")
        if self.depth == self.rate_text_depth:
            if not self.rates[-1]["conditions"]:
                self.rates[-1]["conditions"] = "".join(self.rate_text).strip() or None
            self.rate_text_depth = None
        if self.depth == self.rate_depth:
            self.rate_depth = None
        if self.depth == self.ld_depth:
            try:
                self.documents.append(json.loads("".join(self.ld_buffer)))
            except ValueError:
                pass
            self.ld_depth = None
        self.depth -= 1

    def handle_data(self, value):
        if self.description_depth is not None:
            self.description.append(value)
        if self.ld_depth is not None:
            self.ld_buffer.append(value)
        if self.rate_text_depth is not None:
            self.rate_text.append(value)

    def read(self):
        listing = next((item for item in self.documents if isinstance(item, dict) and item.get("identifier")), {})
        desc = re.sub(r"\n[ \t\n]+\n", "\n\n", "".join(self.description)).strip()
        return listing, desc or listing.get("description"), self.rates


def fetch(path):
    request = Request(ORIGIN + path, headers={"User-Agent": "CasaZen inventory review/1.0"})
    with urlopen(request, timeout=25) as response:
        if urlsplit(response.url).netloc != urlsplit(ORIGIN).netloc:
            raise ValueError("Unexpected provider redirect")
        return response.read(8_000_000).decode("utf-8")


def listing(path, lang, quote):
    query = urlencode({**quote, "lang": lang})
    page = Listing()
    raw = fetch(f"{path}?{query}")
    page.feed(raw)
    source, description, rates = page.read()
    selected = re.search(r"jQuery\(function\(\$\)\s*\{\s*selectRateRadio\((\d+)", raw)
    return source, description, rates, selected[1] if selected else None


def collect(row, quote, pause):
    path = row["sourcePath"]
    if not re.fullmatch(r"/[a-zA-Z0-9_-]+", path):
        raise ValueError("sourcePath must be a single sitemap path")
    override = row.get("quote", {})
    quote = {**quote, **{key: override[key] for key in ("from", "to", "guests", "currency") if key in override}}
    if quote["currency"] not in ("EUR", "CHF") or not (1 <= int(quote["guests"]) <= 100):
        raise ValueError("unsupported quote currency or occupancy")
    quote["guests_rooms"] = f"{quote['guests']},0;"
    source, italian, rates, selected = listing(path, "it", quote)
    time.sleep(pause)
    english_source, english, _, _ = listing(path, "en", quote)
    if not source.get("identifier") or source["identifier"] != english_source.get("identifier"):
        raise ValueError("missing or inconsistent source listing identity")
    if english == italian:
        english = None
    url = ORIGIN + path
    findings = list(row.get("findings", []))
    if path[1:] in UNCONFIRMED_BEDS:
        findings.append({"field": "beds", "status": "confirm", "reason": "Verifica la disposizione dei letti con la fonte.", "source": url})
    if path == "/romantic-chic-apartment":
        findings.append({"field": "cin", "status": "confirm", "reason": "Confronta i CIN della descrizione e della fonte strutturata.", "source": url})
    plans = [
        {
            "id": rate["id"], "name": rate["name"], "currency": quote["currency"], "unit": "night",
            "occupants": int(quote["guests"]), "amount": rate["amount"], "from": quote["from"], "to": quote["to"],
            "conditions": rate["conditions"], "payment": f"{rate['advance']} {quote['currency']} ({rate['percent']}%)",
            "priceKind": "quote",
        } for rate in rates if rate["amount"] > 0
    ]
    selected_rate = next((plan for plan in plans if plan["id"] == selected), None)
    price_source = None
    if selected_rate is None:
        alternative = row.get("alternateNightlyRate") or {}
        alternative_url = urlsplit(alternative.get("sourceUrl", ""))
        alternative_amount = alternative.get("amount")
        if (alternative_url.scheme == "https" and alternative_url.netloc and not alternative_url.username
                and alternative.get("currency") == quote["currency"] and alternative.get("unit") == "night"
                and isinstance(alternative_amount, (float, int)) and not isinstance(alternative_amount, bool)
                and 0 < alternative_amount <= 100_000):
            selected_rate = {"amount": alternative_amount}
            price_source = alternative.get("sourceUrl")
        else:
            findings.append({"field": "nightlyRate", "status": "missing", "reason": "La fonte non espone un prezzo per le date e l’occupazione scelte.", "source": url})
    for text in (italian, english):
        if text and len(text.encode("utf-16-le")) // 2 > 10_000:
            raise ValueError("source description exceeds the configured limit")
    return {
        "orgId": row["orgId"], "propertyId": row["propertyId"],
        "snapshot": {"provider": "kross", "sourceId": source["identifier"], "sourceUrl": url,
                     "parentSourceId": row.get("parentSourceId"), "descriptionIt": italian, "descriptionEn": english,
                     "nightlyRate": selected_rate["amount"] if selected_rate else None, "currency": quote["currency"],
                     "ratePlans": plans, "findings": findings,
                     "unmappedFields": {**row.get("unmappedFields", {}), "quoteSource": url + "?" + urlencode(quote),
                                        **({"nightlyRateSource": price_source} if price_source else {})}},
    }


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--mapping", type=Path, required=True)
    cli.add_argument("--output", type=Path, required=True)
    cli.add_argument("--from", dest="arrival", required=True)
    cli.add_argument("--to", dest="departure", required=True)
    cli.add_argument("--guests", default="2")
    cli.add_argument("--currency", choices=["EUR", "CHF"], default="EUR")
    cli.add_argument("--pause", type=float, default=2)
    args = cli.parse_args()
    mapping = json.loads(args.mapping.read_text())
    sitemap = ElementTree.fromstring(fetch("/sitemap.xml"))
    paths = {urlsplit(node.text).path for node in sitemap.findall(".//{*}loc")}
    quote = {"from": args.arrival, "to": args.departure, "guests_rooms": f"{args.guests},0;",
             "guests": args.guests, "currency": args.currency, "r": 1}
    results, issues, seen = [], [], set()
    for row in mapping:
        path = row["sourcePath"]
        try:
            if path not in paths:
                raise ValueError("listing absent from sitemap")
            entry = collect(row, quote, max(1.0, args.pause))
            identity = (entry["orgId"], entry["snapshot"]["sourceId"])
            if identity in seen:
                raise ValueError("duplicate provider identity for this organization")
            seen.add(identity)
            results.append(entry)
        except (OSError, ValueError, KeyError) as error:
            issues.append({"sourcePath": path, "error": str(error)})
        time.sleep(max(1.0, args.pause))
    args.output.write_text(json.dumps(results, ensure_ascii=False, indent=2) + "\n")
    if issues:
        print(json.dumps(issues, ensure_ascii=False, indent=2), file=sys.stderr)
    return bool(issues)


if __name__ == "__main__":
    sys.exit(main())
