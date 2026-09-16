# Representative acceptance fixtures

`AcceptanceFixtureCorpus` is the single source for the Playwright and portability fixture set. It creates every PDF in a caller-owned temporary directory, so the repository remains free of invoice documents and intentional 20 MiB boundary files.

All values, names, registration identifiers, references, and document text are synthetic. The compact PDF writer is authored in this repository, has no external asset or license dependency, and uses no OCR or OpenAI service. `text-*` fixtures have native text; `scanned-mkd-unknown-confidence` is image-only and is intended for the deterministic OCR path.

| Fixture group | Expected result |
| --- | --- |
| `text-usd`, `text-eur`, `text-gbp-low-confidence`, `scanned-mkd-unknown-confidence` | Valid two-decimal USD, EUR, GBP, and MKD proposals. GBP provides low confidence; MKD provides unknown confidence. |
| `duplicate-primary`, `duplicate-secondary` | Same synthetic supplier/reference pair for duplicate validation. |
| `empty`, `wrong-signature`, `malformed`, `encrypted` | Pre-acceptance failures with their stable PDF error codes. |
| `exact-size`, `over-size`, `exact-page-count`, `over-page-count` | 20 MiB/20 MiB + 1 byte and 25/26 page acceptance boundaries. |
| `processing-failure` | Accepted document with a deterministic `AI_UNAVAILABLE` post-acceptance provider profile. |

Use `MaterializeAsync` once per test data root and pass the returned file to the browser upload control. Provider test doubles should consume the attached `FixtureProposal`; they must not inspect document text or call external OCR/AI services. Corpus tests assert the exact expected proposal values, confidence cases, duplicate pairing, failure categories, and deterministic bytes.
