import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "@fontsource/barlow/400.css";
import "@fontsource/barlow/500.css";
import "@fontsource/barlow/600.css";
import "@fontsource/barlow-condensed/500.css";
import "@fontsource/barlow-condensed/600.css";
import "@fontsource/barlow-condensed/700.css";
import "./styles.css";
import { App } from "./App";
import { Overlay } from "./pages/Overlay";
import { AppProvider } from "./appContext";
import { ToastProvider } from "./components/Toast";

// Block the browser's own context menu and file drops in the packaged app.
window.addEventListener("dragover", (e) => e.preventDefault());
window.addEventListener("drop", (e) => e.preventDefault());
// The page itself never scrolls (styles.css pins it); if the browser scrolls it anyway, put it back at once.
window.addEventListener("scroll", () => {
  if (window.scrollX !== 0 || window.scrollY !== 0) window.scrollTo(0, 0);
});
for (const el of [document.documentElement, document.body]) {
  el.addEventListener("scroll", () => {
    if (el.scrollTop !== 0 || el.scrollLeft !== 0) el.scrollTo(0, 0);
  });
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <ToastProvider>
      <AppProvider>
        {window.location.hash === "#overlay" ? <Overlay /> : <App />}
      </AppProvider>
    </ToastProvider>
  </StrictMode>,
);
