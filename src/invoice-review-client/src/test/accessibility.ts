import axe, { type AxeResults } from 'axe-core';

const releaseBlockingImpacts = new Set(['serious', 'critical']);

export async function assertNoSeriousAccessibilityViolations(container: HTMLElement): Promise<void> {
  const results: AxeResults = await axe.run(container, {
    rules: {
      // jsdom has no layout or computed colour model. Contrast is verified in the
      // documented browser checks instead of producing false results here.
      'color-contrast': { enabled: false },
    },
  });
  const violations = results.violations.filter((violation) =>
    typeof violation.impact === 'string' && releaseBlockingImpacts.has(violation.impact));

  if (violations.length > 0) {
    const details = violations
      .map((violation) => `${violation.id}: ${violation.help} (${violation.nodes.map((node) => node.target.join(' ')).join(', ')})`)
      .join('\n');
    throw new Error(`Serious accessibility violations found:\n${details}`);
  }
}
