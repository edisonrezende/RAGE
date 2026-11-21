import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./index.css";
import App from "./App.jsx";
import MiniPanel from "./pages/MiniPanel.jsx";

// Bloqueia atalhos de navegador que não fazem sentido no app (recarregar, imprimir, zoom).
window.addEventListener("keydown", (e) => {
  const k = e.key.toLowerCase();
  if (e.key === "F5" || (e.ctrlKey && ["r", "p", "u", "+", "-", "=", "0"].includes(k))) e.preventDefault();
});
window.addEventListener("wheel", (e) => { if (e.ctrlKey) e.preventDefault(); }, { passive: false });
window.addEventListener("contextmenu", (e) => { if (!e.target.closest("input, textarea, [data-selectable]")) e.preventDefault(); });

// O painel do mini monitor é a mesma interface aberta numa janela pequena (rota #/mini), sem o menu lateral.
const mini = window.location.hash.startsWith("#/mini");

createRoot(document.getElementById("root")).render(
  <StrictMode>
    {mini ? <MiniPanel /> : <App />}
  </StrictMode>,
);
