// @ts-check
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { createBrowserContext } from "@sunder/sdk/browser";
import { ApiProvider } from "@sunder/sdk/react";
import { Self } from "#sunder/api";
import { App } from "./App.jsx";
import "./styles.css";

const root = document.getElementById("root");
if (!root) throw new Error("The App root element is missing.");
const context = createBrowserContext(Self);
createRoot(root).render(<StrictMode><ApiProvider context={context}><App /></ApiProvider></StrictMode>);
