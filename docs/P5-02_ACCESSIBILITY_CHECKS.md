# P5-02 accessibility and responsive review

Run the frontend regression suite first:

```powershell
Push-Location src/invoice-review-client
npm test -- --run
npm run build
Pop-Location
```

The automated component checks cover accessible names for editable fields and actions, keyboard activation of validation-summary links, focus transfer to blocking fields after validation, focus transfer to save and revalidation failures, explicit terminal-decision confirmation, and duplicate-action disabling. The phase integrator adds `axe-core` as a dev dependency and wires the cross-route axe checks for queue, review, dialogs, failures, and completed records.

Manual checks, using a keyboard only:

1. At 1280 px, Tab through queue filters, upload, table links, review form, PDF controls, validation links, and decision actions. Every focus target has a visible blue ring and labels communicate purpose without color.
2. At 768 px and 320 px, verify the review panels stack, controls remain usable without horizontal page scrolling, dialogs have full-width action targets, and the queue table itself scrolls horizontally while headers stay readable.
3. Open upload, approval, rejection, and dirty-navigation dialogs. Verify focus enters the dialog, Escape/Cancel returns focus to the invoking control, and confirmation buttons cannot be submitted twice while pending.
4. Trigger a validation error, save failure, and revalidation conflict. Verify focus reaches the first affected draft field after validation and the error announcement after a failure; use the summary link to return to the affected field.
5. Verify approved, warning, error, confidence, and processing states are understandable from text labels, not color alone. Amounts, versions, counts, dates, and compact table values use tabular numerals.

Contrast token review: normal foreground/control combinations use the dark ink, blue, emerald, amber, and red 700-level tokens on warm-white/light backgrounds; focus uses the high-contrast blue ring. Red is reserved for blocking/destructive states, while low confidence is amber and unknown confidence is neutral ink.

## Executed browser record

On 2026-09-15, the production-hosted Chromium journey `AccessibilityAndResponsiveJourneyTests` passed at 1280×900, 768×900, and 320×800. It verified no page-level horizontal overflow, visible queue/PDF/form/action layouts, stacked narrow review panels, keyboard activation of the upload dialog, focus entry and Escape focus return, and browser-side axe WCAG 2.2 A/AA scans on queue, upload-dialog, and review states with no serious or critical violations. Screenshots from all three widths were visually inspected for the restrained financial-operations direction and readable responsive layout, then removed with the test artifacts. Validation/error focus and terminal-dialog behavior are additionally covered by the component and failure-journey suites.
