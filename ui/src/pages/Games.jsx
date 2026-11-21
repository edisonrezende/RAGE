import { useMemo, useState } from "react";
import { useApp } from "../App.jsx";
import { call } from "../lib/rage.js";
import { useEvent, useRpc, useTelemetry } from "../lib/store.js";
import * as f from "../lib/format.js";
import { Button, Chip, Empty, ErrorBox, Icon, IconButton, Menu, Panel, Skeleton, Tabs, Toggle, cx } from "../components/ui.jsx";
import { PageIntro } from "../components/Shell.jsx";
import { Keys } from "../components/HotkeyInput.jsx";

const SOURCES = { steam: "Steam", epic: "Epic Games", xbox: "Xbox", gog: "GOG", ubisoft: "Ubisoft", ea: "EA", riot: "Riot", battlenet: "Battle.net", manual: "Adicionado", auto: "Detectado" };
const SOURCE_COLOR = { steam: "#66c0f4", epic: "#e5e5e5", xbox: "#52b043", gog: "#b366ff", ubisoft: "#3d8bff", ea: "#ff4747", riot: "#ff4655", battlenet: "#148eff", manual: "#ff6a2b", auto: "#ff6a2b" };

function GameIcon({ game, size = 48 }) {
  if (game.icon) return <img src={game.icon} alt="" className="shrink-0 rounded-[var(--radius-md)] bg-[var(--bg-layer-2)] object-contain p-1.5" style={{ width: size, height: size }} />;
  const initials = game.name.split(/\s+/).filter(Boolean).slice(0, 2).map((w) => w[0]).join("").toUpperCase();
  return (
    <span className="heading flex shrink-0 items-center justify-center rounded-[var(--radius-md)] font-bold text-white" style={{ width: size, height: size, background: `linear-gradient(135deg, ${SOURCE_COLOR[game.source] || "#555"}, #1a1a1d)`, fontSize: size * 0.34 }}>{initials}</span>
  );
}

function GameCard({ game, onChange }) {
  const { toast } = useApp();
  const [busy, setBusy] = useState(false);
  const update = async (p) => { try { onChange(await call("games.update", { id: game.id, ...p })); } catch (e) { toast(e.message, "danger"); } };
  const play = async (boost) => {
    setBusy(true);
    try { await call("games.launch", { id: game.id, boost }); toast(`Abrindo ${game.name}${boost ? " com Turbo" : ""}…`, "brand"); } catch (e) { toast(e.message, "danger"); }
    setBusy(false);
  };
  return (
    <div className={cx("panel panel-hover flex flex-col p-4", game.running && "border-[#ff6a2b66]")}>
      <div className="flex items-start gap-3">
        <GameIcon game={game} />
        <div className="min-w-0 flex-1">
          <p className="truncate text-[14px] font-semibold" title={game.name}>{game.name}</p>
          <div className="mt-1 flex flex-wrap items-center gap-1.5">
            <Chip><span className="size-1.5 rounded-full" style={{ background: SOURCE_COLOR[game.source] }} />{SOURCES[game.source] || game.source}</Chip>
            {game.running && <Chip tone="brand" dot>Rodando</Chip>}
          </div>
        </div>
        <Menu
          align="end"
          trigger={({ toggle }) => <IconButton icon="more_vert" label="Mais opções" onClick={toggle} />}
          items={[
            { icon: "folder_open", label: "Abrir pasta", onClick: () => call("app.reveal", { path: game.exe || game.folder }) },
            { icon: "play_arrow", label: "Jogar sem Turbo", onClick: () => play(false) },
            { divider: true },
            { icon: "visibility_off", label: game.source === "manual" ? "Remover da biblioteca" : "Ocultar da biblioteca", danger: true, onClick: () => call("games.remove", { id: game.id }).then(onChange) },
          ]}
        />
      </div>
      <p className="mt-3 text-[12px] text-[var(--text-tertiary)]">{f.playTime(game.playSeconds)}{game.lastPlayed ? ` · ${f.rel(game.lastPlayed)}` : ""}</p>
      <div className="mt-3 space-y-2 border-t border-[var(--border)] pt-3 text-[12.5px]">
        <label className="flex cursor-pointer items-center justify-between gap-2">
          <span className="flex items-center gap-1.5 text-[var(--text-secondary)]"><Icon name="rocket_launch" size={16} />Turbo automático</span>
          <Toggle size="sm" checked={game.autoBoost} onChange={(v) => update({ autoBoost: v })} />
        </label>
        <label className="flex cursor-pointer items-center justify-between gap-2" title="Força o Windows a usar a placa de vídeo dedicada neste jogo (notebooks com 2 GPUs)">
          <span className="flex items-center gap-1.5 text-[var(--text-secondary)]"><Icon name="developer_board" size={16} />GPU de alto desempenho</span>
          <Toggle size="sm" checked={game.highPerfGpu} onChange={(v) => update({ highPerfGpu: v })} />
        </label>
      </div>
      <Button variant="brand" className="mt-4 w-full" icon="play_arrow" loading={busy} onClick={() => play(true)} disabled={!game.exists}>Jogar com Turbo</Button>
    </div>
  );
}

function FpsPanel() {
  const { settings, updateSettings, toast } = useApp();
  const s = useTelemetry();
  const { data: fps } = useRpc("fps.status");
  const [overlayOn, setOverlayOn] = useState(false);
  useEvent("overlay", (o) => setOverlayOn(!!o?.visible));
  const show = async () => { try { const r = await call("overlay.toggle", { show: !overlayOn }); setOverlayOn(r.visible); } catch (e) { toast(e.message, "danger"); } };
  const set = async (v) => { await updateSettings(v); call("overlay.applySettings").catch(() => {}); };
  const game = s?.game;
  return (
    <Panel title="FPS e overlay de jogo" icon="speed" subtitle="Contador de FPS sem injeção (ETW, como o PresentMon)">
      <div className="grid gap-4 lg:grid-cols-[220px_1fr]">
        <div className="flex flex-col items-center justify-center rounded-[var(--radius-lg)] bg-[var(--bg-subtle)] p-4 text-center">
          {game?.fps != null ? (
            <>
              <span className="heading brand-text text-[44px] leading-none font-bold tabular">{f.n(game.fps)}</span>
              <span className="mt-1 text-[12px] font-semibold text-[var(--text-secondary)]">FPS · {game.name}</span>
              <span className="text-[11.5px] text-[var(--text-tertiary)]">1% low {f.n(game.low1)} · {f.n(game.frameTime, 1)} ms</span>
            </>
          ) : (
            <>
              <Icon name={fps?.running ? "sports_esports" : "block"} size={30} className="text-[var(--text-tertiary)]" />
              <p className="mt-2 text-[12.5px] font-semibold">{fps?.running ? "Aguardando um jogo" : "Contador de FPS desligado"}</p>
              <p className="text-[11.5px] text-[var(--text-tertiary)]">{fps?.running ? "Abra um jogo DirectX/Vulkan e ele aparece aqui." : fps?.admin === false ? "Precisa do RAGE! como administrador." : fps?.error || "Ative abaixo."}</p>
            </>
          )}
        </div>
        <div className="divide-y divide-[var(--border)] text-[13px]">
          <label className="flex items-center justify-between gap-3 py-2.5">
            <div><p className="font-semibold">Contador de FPS</p><p className="text-[12px] text-[var(--text-tertiary)]">Mede o FPS do app em primeiro plano. Reinicie o RAGE! após alterar.</p></div>
            <Toggle checked={settings?.fpsCounter} onChange={(v) => set({ fpsCounter: v })} />
          </label>
          <label className="flex items-center justify-between gap-3 py-2.5">
            <div><p className="font-semibold">Overlay ao jogar</p><p className="text-[12px] text-[var(--text-tertiary)]">FPS, CPU, GPU e RAM por cima do jogo, em janela ou tela cheia sem bordas.</p></div>
            <Toggle checked={settings?.overlayEnabled} onChange={(v) => set({ overlayEnabled: v, overlayAutoInGame: v ? true : settings?.overlayAutoInGame })} />
          </label>
          <div className="flex items-center justify-between gap-3 py-2.5">
            <div><p className="font-semibold">Posição e atalho</p><p className="text-[12px] text-[var(--text-tertiary)]">Mostre/esconda a qualquer momento com <Keys combo={settings?.overlayHotkey || "Ctrl+Shift+F10"} className="align-middle" /> (troque em Configurações).</p></div>
            <div className="flex items-center gap-2">
              <Tabs value={settings?.overlayPosition || "top-left"} onChange={(v) => set({ overlayPosition: v })} items={[{ value: "top-left", label: "↖" }, { value: "top-right", label: "↗" }, { value: "bottom-left", label: "↙" }, { value: "bottom-right", label: "↘" }]} />
              <Button size="sm" onClick={show} icon={overlayOn ? "visibility_off" : "visibility"}>{overlayOn ? "Esconder" : "Testar"}</Button>
            </div>
          </div>
        </div>
      </div>
    </Panel>
  );
}

export default function Games() {
  const { toast } = useApp();
  const { data, setData, error, loading, reload } = useRpc("games.list");
  const [scanning, setScanning] = useState(false);
  const [q, setQ] = useState("");
  const [tab, setTab] = useState("all");
  useEvent("game", () => reload(true));
  const scan = async () => {
    setScanning(true);
    try { const r = await call("games.scan"); setData(r); toast(`${r.length} jogos na biblioteca`, "success"); } catch (e) { toast(e.message, "danger"); }
    setScanning(false);
  };
  const add = async () => {
    const files = await call("app.pickFiles", { title: "Escolha o executável do jogo", multi: false, filter: "Programas (*.exe)|*.exe" });
    if (!files?.length) return;
    try { setData(await call("games.add", { exe: files[0] })); toast("Jogo adicionado", "success"); } catch (e) { toast(e.message, "danger"); }
  };
  const list = useMemo(() => {
    let l = data || [];
    if (tab !== "all") l = l.filter((g) => g.source === tab);
    if (q.trim()) l = l.filter((g) => g.name.toLowerCase().includes(q.toLowerCase()));
    return [...l].sort((a, b) => (b.running - a.running) || (new Date(b.lastPlayed || 0) - new Date(a.lastPlayed || 0)) || a.name.localeCompare(b.name));
  }, [data, tab, q]);
  const sources = [...new Set((data || []).map((g) => g.source))];
  const total = (data || []).reduce((a, g) => a + (g.playSeconds || 0), 0);

  return (
    <div className="space-y-4">
      <PageIntro icon="stadia_controller" title="Seus jogos" actions={<><Button icon="add" onClick={add}>Adicionar jogo</Button><Button variant="brand" icon="travel_explore" onClick={scan} loading={scanning}>Procurar jogos</Button></>}>
        Encontrados automaticamente na Steam, Epic, Xbox, GOG, Ubisoft, EA, Riot e Battle.net. Quando um jogo abre, o RAGE! liga o Turbo e desliga quando ele fecha.
        {total > 3600 && <> Você já jogou <b>{f.n(total / 3600, 0)} horas</b> com o RAGE! aberto.</>}
      </PageIntro>
      <FpsPanel />
      <ErrorBox error={error} onRetry={reload} />
      <div className="flex flex-wrap items-center gap-3">
        <Tabs value={tab} onChange={setTab} items={[{ value: "all", label: "Todos", badge: data?.length }, ...sources.map((s) => ({ value: s, label: SOURCES[s] || s, badge: data.filter((g) => g.source === s).length }))]} />
        <div className="ml-auto flex h-8 w-64 items-center gap-2 rounded-[var(--radius-md)] border border-[var(--border)] bg-[var(--bg-surface)] px-2.5">
          <Icon name="search" size={17} className="text-[var(--text-tertiary)]" />
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Buscar jogo…" className="min-w-0 flex-1 bg-transparent text-[13px] outline-none placeholder:text-[var(--text-muted)]" />
        </div>
      </div>
      {loading && !data ? (
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">{Array.from({ length: 4 }, (_, i) => <Skeleton key={i} className="h-60" />)}</div>
      ) : list.length === 0 ? (
        <Panel><Empty icon="sports_esports" title={q ? "Nenhum jogo com esse nome" : "Nenhum jogo encontrado ainda"} action={<Button variant="brand" icon="travel_explore" onClick={scan} loading={scanning}>Procurar jogos</Button>}>Clique em Procurar jogos ou adicione o executável manualmente.</Empty></Panel>
      ) : (
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">{list.map((g) => <GameCard key={g.id} game={g} onChange={setData} />)}</div>
      )}
    </div>
  );
}
