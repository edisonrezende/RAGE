// Arranjo da Área de Trabalho: os atalhos de programas viram um bloco em degradê de cor no canto de cima,
// com apps do mesmo fornecedor lado a lado; pastas, arquivos e a Lixeira descem para o canto de baixo, à direita.
// É cálculo puro: a tela usa para a prévia e manda o resultado pronto para o RAGE! aplicar.

const clamp = (v, a, b) => Math.max(a, Math.min(b, v));

/** Média circular dos matizes (em graus), pesada pela saturação. */
function meanHue(items) {
  let x = 0, y = 0;
  for (const i of items) {
    const w = Math.max(0.05, i.sat ?? 0.5), a = (i.hue * Math.PI) / 180;
    x += Math.cos(a) * w; y += Math.sin(a) * w;
  }
  return ((Math.atan2(y, x) * 180) / Math.PI + 360) % 360;
}

/** Diferença de matiz com sinal, entre -180 e 180. */
const hueDelta = (a, b) => ((a - b + 540) % 360) - 180;

export const defaultRows = (count, grid) => clamp(Math.round(Math.sqrt(count * 1.6)), Math.min(3, grid.rows), grid.rows);

/**
 * items: saída de desktop.scan. grid: { cols, rows }. opts: { rows, groupVendors }.
 * Devolve { placements: [{ id, col, row }], byId, rows, blockCols, vendors, unplaced }.
 */
export function planDesktop(items, grid, opts = {}) {
  const groupVendors = opts.groupVendors !== false;
  const apps = items.filter((i) => i.kind === "app");
  const rest = items.filter((i) => i.kind !== "app");
  let rows = clamp(opts.rows ?? defaultRows(apps.length, grid), 1, grid.rows);
  if (Math.ceil(apps.length / rows) > grid.cols) rows = grid.rows;

  // 1) Unidades: fornecedor com mais de um app anda junto; o resto anda sozinho.
  const buckets = new Map();
  for (const a of apps) {
    const key = groupVendors && a.vendorKey ? `v:${a.vendorKey}` : `i:${a.id}`;
    if (!buckets.has(key)) buckets.set(key, []);
    buckets.get(key).push(a);
  }
  const units = [...buckets.values()].map((members) => {
    const colored = members.filter((m) => !m.neutral);
    const hue = colored.length ? meanHue(colored) : 0;
    members.sort((a, b) => (a.neutral - b.neutral) || (a.neutral ? b.lum - a.lum : hueDelta(a.hue, hue) - hueDelta(b.hue, hue)) || a.name.localeCompare(b.name));
    return { members, neutral: colored.length === 0, hue, lum: members.reduce((s, m) => s + m.lum, 0) / members.length };
  });

  // 2) Ordem do degradê: pelo matiz, começando depois do maior salto (assim o bloco não "dá a volta" no meio).
  let colored = units.filter((u) => !u.neutral).sort((a, b) => a.hue - b.hue);
  if (colored.length > 1) {
    let start = 0, gap = -1;
    colored.forEach((u, i) => {
      const next = colored[(i + 1) % colored.length].hue + (i === colored.length - 1 ? 360 : 0);
      if (next - u.hue > gap) { gap = next - u.hue; start = (i + 1) % colored.length; }
    });
    colored = [...colored.slice(start), ...colored.slice(0, start)];
  }
  const neutrals = units.filter((u) => u.neutral).sort((a, b) => b.lum - a.lum);

  // 3) Enche coluna por coluna. Se um grupo não cabe no resto da coluna, tenta puxar um vizinho próximo que caiba.
  const queue = [...colored, ...neutrals];
  const order = [];
  while (queue.length) {
    const free = rows - (order.length % rows);
    let pick = 0;
    const size = queue[0].members.length;
    if (size > free && size <= rows) {
      const j = queue.findIndex((u, idx) => idx > 0 && idx <= 3 && u.members.length <= free);
      if (j > 0) pick = j;
    }
    order.push(...queue.splice(pick, 1)[0].members);
  }

  const placements = [];
  const capacity = rows * grid.cols;
  order.slice(0, capacity).forEach((a, k) => placements.push({ id: a.id, col: Math.floor(k / rows), row: k % rows }));
  const blockCols = Math.ceil(Math.min(order.length, capacity) / rows);

  // 4) O que não é programa: do canto de baixo à direita para cima, Lixeira primeiro, depois atalhos, pastas e arquivos.
  const rank = { system: 0, shortcut: 1, folder: 2, file: 3 };
  const tail = [...rest].sort((a, b) => (rank[a.kind] ?? 9) - (rank[b.kind] ?? 9) || (a.ext || "").localeCompare(b.ext || "") || a.name.localeCompare(b.name));
  const used = new Set(placements.map((p) => `${p.col},${p.row}`));
  let col = grid.cols - 1, row = grid.rows - 1, unplaced = Math.max(0, order.length - capacity);
  for (const it of tail) {
    while (col >= 0 && used.has(`${col},${row}`)) { row--; if (row < 0) { row = grid.rows - 1; col--; } }
    if (col < 0) { unplaced++; continue; }
    placements.push({ id: it.id, col, row });
    used.add(`${col},${row}`);
  }

  const vendors = units.filter((u) => u.members.length > 1)
    .map((u) => ({ key: u.members[0].vendorKey, name: u.members[0].vendor || u.members[0].vendorKey, count: u.members.length, color: u.members.find((m) => !m.neutral)?.color || u.members[0].color }))
    .sort((a, b) => b.count - a.count);

  return { placements, byId: new Map(placements.map((p) => [p.id, p])), rows, blockCols, vendors, unplaced, apps: apps.length, rest: rest.length };
}
