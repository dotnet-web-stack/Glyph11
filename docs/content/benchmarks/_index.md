---
title: Benchmarks
layout: wide
toc: false
---

Performance tracking across commits. Updated on each manual benchmark run on `main`. Regressions over 15% automatically fail PR checks.

## Latest Results

<div id="benchmark-tables"><p><em>Loading baseline data…</em></p></div>

{{< callout type="info" >}}
These numbers are from CI runs (`ubuntu-latest`) and may differ from local results.
{{< /callout >}}

<script src="/Glyph11/benchmarks/data.js"></script>
<script>
(function () {
  const container = document.getElementById('benchmark-tables');

  if (!window.BENCHMARK_DATA) {
    container.innerHTML = '<p><em>Could not load baseline data.</em></p>';
    return;
  }

  const entries = window.BENCHMARK_DATA.entries['Benchmark'];
  const latest = entries[entries.length - 1];
  const benches = latest.benches;

  function val(name) {
    const b = benches.find(b => b.name === name);
    return b ? b.value : null;
  }

  function fmt(v) {
    if (v === null) return '—';
    return v >= 1000
      ? v.toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 })
      : v.toFixed(1);
  }

  function fmtAlloc(name) {
    const v = val(name + '.Allocated');
    if (v === null) return '—';
    if (v === 0) return '0 B';
    if (v < 1024) return v.toFixed(0) + ' B';
    return (v / 1024).toFixed(1) + ' KB';
  }

  function makeTable(headers, rows) {
    const hdr = '<tr>' + headers.map(h => '<th>' + h + '</th>').join('') + '</tr>';
    const body = rows.map(r =>
      '<tr>' + r.map((c, i) => i === 0 ? '<td>' + c + '</td>' : '<td style="text-align:right">' + c + '</td>').join('') + '</tr>'
    ).join('');
    return '<table>' + hdr + body + '</table>';
  }

  // --- Parser Benchmarks ---
  const parserSizes = [
    ['Small', 'Small_ROM', 'Small_MultiSegment'],
    ['4 KB',  'Header4K_ROM', 'Header4K_MultiSegment'],
    ['32 KB', 'Header32K_ROM', 'Header32K_MultiSegment'],
  ];
  const parserRows = parserSizes.map(([label, rom, ms]) => [
    label,
    fmt(val('Benchmarks.FlexibleParserBenchmark.' + rom)) + ' ns',
    fmtAlloc('Benchmarks.FlexibleParserBenchmark.' + rom),
    fmt(val('Benchmarks.FlexibleParserBenchmark.' + ms)) + ' ns',
    fmtAlloc('Benchmarks.FlexibleParserBenchmark.' + ms),
    fmt(val('Benchmarks.UltraHardenedParserBenchmark.' + rom)) + ' ns',
    fmtAlloc('Benchmarks.UltraHardenedParserBenchmark.' + rom),
    fmt(val('Benchmarks.UltraHardenedParserBenchmark.' + ms)) + ' ns',
    fmtAlloc('Benchmarks.UltraHardenedParserBenchmark.' + ms),
  ]);

  container.innerHTML =
    '<h3>Parser Benchmarks</h3>' +
    makeTable(
      ['Payload', 'Flexible (ROM)', 'Alloc', 'Flexible (MultiSeg)', 'Alloc', 'Ultra (ROM)', 'Alloc', 'Ultra (MultiSeg)', 'Alloc'],
      parserRows
    );
})();
</script>

## Trend Chart

<iframe src="/Glyph11/benchmarks/chart.html" width="100%" height="600" frameborder="0" style="border: 1px solid #e5e7eb; border-radius: 8px;"></iframe>

{{< callout type="info" >}}
The chart requires at least two data points to show trend lines. Run the benchmark workflow manually on `main` to add new data points.
{{< /callout >}}
