"use strict";

import powerbi from "powerbi-visuals-api";
import "./../style/visual.less";

import VisualConstructorOptions = powerbi.extensibility.visual.VisualConstructorOptions;
import VisualUpdateOptions = powerbi.extensibility.visual.VisualUpdateOptions;
import EnumerateVisualObjectInstancesOptions = powerbi.EnumerateVisualObjectInstancesOptions;
import VisualObjectInstanceEnumeration = powerbi.VisualObjectInstanceEnumeration;
import IVisual = powerbi.extensibility.visual.IVisual;
import IVisualEventService = powerbi.extensibility.IVisualEventService;

const DEFAULT_URL = "http://localhost:5211/api/dax/chat?ws=WORKSPACE&ds=DATASET";

const C = {
    blue:     "#1E72B8",
    teal:     "#4ABFBF",
    gray:     "#7A96A8",
    bgLight:  "#F4F7FA",
    bgUser:   "#1E72B8",
    bgBot:    "#FFFFFF",
    border:   "#D6E4F0",
    text:     "#1A2B3C",
    textLight:"#FFFFFF",
    textMuted:"#7A96A8",
};

export class Visual implements IVisual {
    private events: IVisualEventService;
    private target: HTMLElement;
    private input: HTMLInputElement;
    private button: HTMLButtonElement;
    private chatLog: HTMLDivElement;
    private loading: HTMLDivElement;
    private apiUrl: string = DEFAULT_URL;

    constructor(options: VisualConstructorOptions) {
        this.events = options.host.eventService;
        this.target = options.element;
        this.buildUI();
    }

    private buildUI(): void {
        while (this.target.firstChild) this.target.removeChild(this.target.firstChild);

        this.target.style.cssText = `
            font-family: Segoe UI, sans-serif;
            height: 100%;
            display: flex;
            flex-direction: column;
            background: ${C.bgLight};
            border-radius: 8px;
            overflow: hidden;
            box-sizing: border-box;
        `;

        // Header
        const header = document.createElement("div");
        header.style.cssText = `
            background: linear-gradient(135deg, ${C.blue} 0%, #155a94 100%);
            padding: 10px 14px;
            display: flex;
            align-items: center;
            gap: 10px;
            flex-shrink: 0;
        `;

        const dot1 = this.mkDot(C.teal);
        const dot2 = this.mkDot("rgba(255,255,255,0.4)");
        const dot3 = this.mkDot("rgba(255,255,255,0.2)");

        const titleWrap = document.createElement("div");
        titleWrap.style.cssText = "flex:1;";
        const titleEl = document.createElement("div");
        titleEl.textContent = "IAX Chat";
        titleEl.style.cssText = `color:${C.textLight};font-weight:700;font-size:14px;letter-spacing:0.5px;`;
        const subtitle = document.createElement("div");
        subtitle.textContent = "Asistente de datos";
        subtitle.style.cssText = `color:rgba(255,255,255,0.65);font-size:10px;margin-top:1px;`;
        titleWrap.appendChild(titleEl);
        titleWrap.appendChild(subtitle);

        header.appendChild(dot1);
        header.appendChild(dot2);
        header.appendChild(dot3);
        header.appendChild(titleWrap);
        this.target.appendChild(header);

        // Chat log
        this.chatLog = document.createElement("div");
        this.chatLog.style.cssText = `
            flex: 1;
            overflow-y: auto;
            padding: 12px;
            display: flex;
            flex-direction: column;
            gap: 10px;
        `;
        this.target.appendChild(this.chatLog);

        // Loading
        this.loading = document.createElement("div");
        this.loading.style.cssText = `
            display: none;
            padding: 4px 14px 8px;
            flex-shrink: 0;
        `;
        const dots = document.createElement("div");
        dots.style.cssText = `display:flex;gap:4px;align-items:center;`;
        for (let i = 0; i < 3; i++) {
            const d = document.createElement("div");
            d.style.cssText = `
                width:6px;height:6px;border-radius:50%;
                background:${C.teal};
                animation:iaxPulse 1.2s ease-in-out ${i * 0.2}s infinite;
            `;
            dots.appendChild(d);
        }
        this.loading.appendChild(dots);
        this.target.appendChild(this.loading);

        // Input bar
        const inputBar = document.createElement("div");
        inputBar.style.cssText = `
            padding: 10px 12px;
            background: ${C.bgBot};
            border-top: 1px solid ${C.border};
            display: flex;
            gap: 8px;
            flex-shrink: 0;
        `;

        this.input = document.createElement("input");
        this.input.type = "text";
        this.input.placeholder = "Escribe tu pregunta...";
        this.input.style.cssText = `
            flex: 1;
            padding: 8px 12px;
            border: 1.5px solid ${C.border};
            border-radius: 20px;
            font-size: 12px;
            outline: none;
            background: ${C.bgLight};
            color: ${C.text};
            transition: border-color 0.2s;
        `;
        this.input.addEventListener("focus", () => { this.input.style.borderColor = C.teal; });
        this.input.addEventListener("blur",  () => { this.input.style.borderColor = C.border; });
        this.input.addEventListener("keydown", (e) => { if (e.key === "Enter") this.sendQuestion(); });

        this.button = document.createElement("button");
        this.button.textContent = "➤";
        this.button.title = "Enviar";
        this.button.style.cssText = `
            width: 36px; height: 36px;
            background: linear-gradient(135deg, ${C.blue}, ${C.teal});
            color: white;
            border: none;
            border-radius: 50%;
            cursor: pointer;
            display: flex;
            align-items: center;
            justify-content: center;
            flex-shrink: 0;
            transition: opacity 0.2s;
        `;
        this.button.addEventListener("click", () => this.sendQuestion());

        inputBar.appendChild(this.input);
        inputBar.appendChild(this.button);
        this.target.appendChild(inputBar);

        // CSS animation
        const style = document.createElement("style");
        style.textContent = `
            @keyframes iaxPulse {
                0%,80%,100% { transform: scale(0.6); opacity:0.4; }
                40% { transform: scale(1); opacity:1; }
            }
        `;
        this.target.appendChild(style);
    }

    private mkDot(color: string): HTMLDivElement {
        const d = document.createElement("div");
        d.style.cssText = `width:7px;height:7px;border-radius:50%;background:${color};flex-shrink:0;`;
        return d;
    }

    private addBubble(text: string, role: "user" | "bot"): void {
        const isUser = role === "user";
        const wrap = document.createElement("div");
        wrap.style.cssText = `display:flex;justify-content:${isUser ? "flex-end" : "flex-start"};`;

        const bubble = document.createElement("div");
        bubble.style.cssText = `
            max-width: 88%;
            padding: 10px 14px;
            border-radius: ${isUser ? "16px 16px 4px 16px" : "16px 16px 16px 4px"};
            font-size: 12px;
            line-height: 1.6;
            word-break: break-word;
            background: ${isUser ? `linear-gradient(135deg, ${C.blue}, #155a94)` : C.bgBot};
            color: ${isUser ? C.textLight : C.text};
            border: ${isUser ? "none" : `1px solid ${C.border}`};
            box-shadow: 0 1px 4px rgba(0,0,0,0.10);
        `;

        if (isUser) {
            bubble.textContent = text;
        } else {
            this.renderMarkdown(text, bubble);
        }

        wrap.appendChild(bubble);
        this.chatLog.appendChild(wrap);
        this.chatLog.scrollTop = this.chatLog.scrollHeight;
    }

    private renderMarkdown(text: string, container: HTMLElement): void {
        const lines = text.split("\n");
        let inList = false;
        let listEl: HTMLElement | null = null;

        const closeList = () => { inList = false; listEl = null; };

        const applyInline = (line: string, parent: HTMLElement): void => {
            // Bold: **text**
            const parts = line.split(/(\*\*[^*]+\*\*)/g);
            parts.forEach(p => {
                if (p.startsWith("**") && p.endsWith("**")) {
                    const b = document.createElement("strong");
                    b.textContent = p.slice(2, -2);
                    b.style.color = C.blue;
                    parent.appendChild(b);
                } else if (p) {
                    parent.appendChild(document.createTextNode(p));
                }
            });
        };

        lines.forEach(raw => {
            const line = raw.trimEnd();

            // Blank line
            if (!line.trim()) {
                closeList();
                const sp = document.createElement("div");
                sp.style.height = "4px";
                container.appendChild(sp);
                return;
            }

            // H1/H2: # or ##
            if (/^#{1,2} /.test(line)) {
                closeList();
                const h = document.createElement("div");
                h.style.cssText = `font-weight:700;font-size:13px;color:${C.blue};margin:6px 0 3px;border-bottom:1px solid ${C.border};padding-bottom:3px;`;
                h.textContent = line.replace(/^#+\s/, "");
                container.appendChild(h);
                return;
            }

            // Numbered list: 1. item
            if (/^\d+\.\s/.test(line)) {
                if (!inList) {
                    listEl = document.createElement("ol");
                    listEl.style.cssText = `margin:4px 0;padding-left:18px;`;
                    container.appendChild(listEl);
                    inList = true;
                }
                const li = document.createElement("li");
                li.style.cssText = `margin:3px 0;`;
                applyInline(line.replace(/^\d+\.\s/, ""), li);
                listEl!.appendChild(li);
                return;
            }

            // Bullet list: - or *
            if (/^[-*•]\s/.test(line)) {
                if (!inList) {
                    listEl = document.createElement("ul");
                    listEl.style.cssText = `margin:4px 0;padding-left:16px;list-style:none;`;
                    container.appendChild(listEl);
                    inList = true;
                }
                const li = document.createElement("li");
                li.style.cssText = `margin:3px 0;display:flex;gap:6px;align-items:baseline;`;
                const dot = document.createElement("span");
                dot.textContent = "•";
                dot.style.cssText = `color:${C.teal};font-size:14px;flex-shrink:0;`;
                li.appendChild(dot);
                const txt = document.createElement("span");
                applyInline(line.replace(/^[-*•]\s/, ""), txt);
                li.appendChild(txt);
                listEl!.appendChild(li);
                return;
            }

            // Normal paragraph
            closeList();
            const p = document.createElement("div");
            p.style.cssText = `margin:2px 0;`;
            applyInline(line, p);
            container.appendChild(p);
        });
    }

    private async sendQuestion(): Promise<void> {
        const question = this.input.value.trim();
        if (!question) return;

        let baseUrl: string;
        let workspaceName: string;
        let datasetName: string;
        try {
            const parsed = new URL(this.apiUrl);
            workspaceName = parsed.searchParams.get("ws") || "";
            datasetName   = parsed.searchParams.get("ds") || "";
            parsed.searchParams.delete("ws");
            parsed.searchParams.delete("ds");
            baseUrl = parsed.toString();
        } catch {
            this.addBubble("URL inválida. Configura en el panel de formato: http://servidor/api/dax/chat?ws=WORKSPACE&ds=DATASET", "bot");
            return;
        }

        if (!workspaceName || !datasetName) {
            this.addBubble("La URL debe incluir ?ws=WORKSPACE&ds=DATASET", "bot");
            return;
        }

        this.addBubble(question, "user");
        this.input.value = "";
        this.button.disabled = true;
        this.loading.style.display = "block";

        try {
            const response = await fetch(baseUrl, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ question, workspaceName, datasetName })
            });

            if (!response.ok) {
                let errMsg = "Error al procesar la consulta.";
                try {
                    const errBody = await response.json();
                    errMsg = errBody?.message ?? errBody?.title ?? errMsg;
                } catch { /* respuesta no es JSON */ }
                this.addBubble("⚠️ " + errMsg, "bot");
                return;
            }

            const data = await response.json();
            this.addBubble(data.answer ?? JSON.stringify(data, null, 2), "bot");
        } catch {
            this.addBubble("Error de conexión con la API.", "bot");
        } finally {
            this.button.disabled = false;
            this.loading.style.display = "none";
        }
    }

    public update(options: VisualUpdateOptions): void {
        this.events.renderingStarted(options);
        const objects = options.dataViews?.[0]?.metadata?.objects;
        if (objects?.["apiSettings"]?.["apiUrl"]) {
            this.apiUrl = objects["apiSettings"]["apiUrl"] as string;
        }
        if (!this.input) this.buildUI();
        this.events.renderingFinished(options);
    }

    public enumerateObjectInstances(options: EnumerateVisualObjectInstancesOptions): VisualObjectInstanceEnumeration {
        return [{
            objectName: options.objectName,
            properties: { apiUrl: this.apiUrl },
            selector: null
        }];
    }
}
