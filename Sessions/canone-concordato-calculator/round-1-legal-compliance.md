## Legal & Compliance Advisor — Round 1 Response

**Vote**: OBJECT

**Reasoning**:
The spec is lawful to keep as frozen prep: it does not authorize a build, does not auto-file IMU or RLI, and does not let CasaZen issue an attestazione di conformità. Facilitator framing (DPR 322/1998 left to `spec-ltr-rli-registration`; AC8 export/preview only; AC7 contact-list only) is consistent with `.claude/context/regulations/canone_concordato.md` and business-analysis §6. That is why this is not REJECT.

OBJECT is required because the spec still presents unverified legal facts as product truth and omits in-scope `[COUNSEL_REQUIRED]` items from research §11 / business-analysis §6. Overview states Seveso and Cesano Maderno “both qualify” for ATA benefits; AC2 seeds them into `HighTensionAreaComune`; AC3’s pass condition asserts `CedolareIrpefRegistroBenefits.Applies = true (Seveso is ATA)`. Research §1.3 and §11 gaps 1–3 state that presence rests on converging secondary sources, not the Delibera CIPE 87/2003 primary text, and that the local “alta densità” list is not the national ATA list. `VerifiedDirectly` exists on AC2 but is not a hard gate on `Applies`. A `[COUNSEL_REQUIRED]` that does not change AC behavior is a label, not a gate.

AC3 also omits zone / foglio catastale while Cesano Maderno has two distinct band tables (research §3). Returning a numeric range without zone is a silent guess, contrary to AC5’s honesty rule. IMU `Applies = true` “whenever the contract is genuinely concordato” omits the attestazione condition that the regulation note and Cesano Art. 13 treat as a condition for the benefit, not a later UX hint.

Dependency on unimplemented `spec-ltr-rli-registration` (AC6/AC8/AC9) is honestly declared and is not itself unlawful for frozen prep. 1-vs-2 signatories and Cesano ≈0,78% labeling are correctly flagged and must stay unencoded as fact. Residual counsel items below must be gates, not footnotes, before any seed is treated as product truth.

Not legal advice.

**Details**:

| Issue | H/M/L | Mitigation (required in spec) | Counsel Y/N |
|---|---|---|---|
| ATA Seveso/Cesano seeded as fact without CIPE/AdE primary text (research §11 #1–3) | H | `VerifiedDirectly=false` ⇒ ATA benefits not `Applies=true`; UI = pending, not “qualifies” | Y |
| 1 vs 2 attestazione signatories in MB (research §11 #9) | M | Keep AC7 as contacts only; do not encode a validity rule | Y |
| Cesano IMU ≈0,78% derived + 2026 delibera missing (research §6, §11 #8) | M | Label derived + tax year; do not show as official aliquota | Y |
| MB agreement currency / 18-month term (research §11 #4) — Open Question only | H | Regulatory gate before `DataCompleteness=Complete` seed | Y |
| Seveso reception delibera not found (research §11 #5) | M | Do not treat Seveso coverage as confirmed until verified | Y |
| Cesano zone matching without planimetrie / no zone on AC3 (research §3, §11 #6) | H | Require zone or foglio; else `Available=false`, no blended range | Y (zone rule); N (cartography ops) |
| Seveso dual IMU channel (research §11 #7) | M | AC8 must not pick one channel as “the” official path | Y |
| Attestazione as condition for all benefits, incl. IMU (reg note; Cesano Art. 13) | H | AC3 `Applies` = theoretical eligibility, not acquired benefit | Y (wording) |
| AC6/AC8/AC9 depend on unimplemented RLI spec | L | Keep as SPEC-ONLY; calculator-only ship still needs disclaimer + ATA gate | N for this spec; Y on RLI spec (DPR 322) |
| Unauthorized intermediation / tax advice | L as written | Keep export-only, no auto-send, no attestazione issuance, verbatim “informativa, non consulenza fiscale” | N on this spec if those constraints hold; Y on RLI filing |

**Concrete spec changes required**

1. **Overview + AC2 + AC3 VO**: Replace “both qualify” with secondary-source status. Until counsel confirms the official AdE/CIPE ATA list, `CedolareIrpefRegistroBenefits.Applies` must not be `true` solely because the row is seeded. AC3 must not pass on “Seveso is ATA.” Use `VerifiedDirectly` as a hard gate (or a distinct `Unverified`/`PendingCounsel` state). Same split of lists as AC2 is correct; the seed content is not.

2. **AC3 inputs**: Add zone or foglio catastale. For Cesano Maderno (two zones, research §3), missing zone ⇒ `Available=false` + Reason, never a numeric min/max. Seveso’s single-zone example (3.445–5.525 €) may stay as an illustration only after coefficients are explicit; do not generalize it to Cesano.

3. **AC3 `FiscalBenefits`**: `ImuReduction.Applies` / ATA `Applies` must not mean the landlord has obtained the benefit. Surface that, for non-assisted contracts, attestazione di conformità is a condition for all agevolazioni (reg note; Cesano regolamento Art. 13). CasaZen still does not issue it (AC7).

4. **Regulatory / Legal Gates — add**:
   - `[COUNSEL_REQUIRED]` MB territorial agreement still in force at seed time (research §11 #4).
   - `[COUNSEL_REQUIRED]` Seveso formal reception of the MB agreement (research §11 #5).
   - `[COUNSEL_REQUIRED]` Seveso IMU notification channel currently valid (research §11 #7).
   - `[COUNSEL_REQUIRED]` Cesano IMU figure is derived from the 2025 “Altri fabbricati” 1,04% delibera; 2026 not deliberated at research date (research §11 #8).

5. **Keep as-is**: AC7 1-vs-2; Cesano ≈0,78% never as official aliquota; Out of Scope on auto-send and CasaZen attestazione; AC6 as DTO-only; DPR 322/1998 remains owned by the RLI spec.
