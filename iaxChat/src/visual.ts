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

export class Visual implements IVisual {
    private events: IVisualEventService;
    private target: HTMLElement;
    private input: HTMLInputElement;
    private button: HTMLButtonElement;
    private answerBox: HTMLDivElement;
    private loading: HTMLDivElement;
    private apiUrl: string = DEFAULT_URL;

    constructor(options: VisualConstructorOptions) {
        this.events = options.host.eventService;
        this.target = options.element;
        this.buildUI();
    }

    private buildUI(): void {
        while (this.target.firstChild) this.target.removeChild(this.target.firstChild);
        this.target.style.cssText = "font-family:Segoe UI,sans-serif;padding:12px;box-sizing:border-box;height:100%;display:flex;flex-direction:column;gap:8px;";

        const title = document.createElement("div");
        title.textContent = "IAX Chat";
        title.style.cssText = "font-weight:600;font-size:14px;color:#333;";
        this.target.appendChild(title);

        const row = document.createElement("div");
        row.style.cssText = "display:flex;gap:6px;";

        this.input = document.createElement("input");
        this.input.type = "text";
        this.input.placeholder = "Pregunta sobre los datos...";
        this.input.style.cssText = "flex:1;padding:6px 10px;border:1px solid #ccc;border-radius:4px;font-size:13px;";
        this.input.addEventListener("keydown", (e) => { if (e.key === "Enter") this.sendQuestion(); });

        this.button = document.createElement("button");
        this.button.textContent = "Enviar";
        this.button.style.cssText = "padding:6px 14px;background:#0078d4;color:#fff;border:none;border-radius:4px;cursor:pointer;font-size:13px;";
        this.button.addEventListener("click", () => this.sendQuestion());

        row.appendChild(this.input);
        row.appendChild(this.button);
        this.target.appendChild(row);

        this.loading = document.createElement("div");
        this.loading.textContent = "Consultando...";
        this.loading.style.cssText = "font-size:12px;color:#888;display:none;";
        this.target.appendChild(this.loading);

        this.answerBox = document.createElement("div");
        this.answerBox.style.cssText = "flex:1;overflow-y:auto;font-size:13px;color:#333;white-space:pre-wrap;border-top:1px solid #eee;padding-top:8px;";
        this.target.appendChild(this.answerBox);
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
            datasetName = parsed.searchParams.get("ds") || "";
            parsed.searchParams.delete("ws");
            parsed.searchParams.delete("ds");
            baseUrl = parsed.toString();
        } catch {
            this.answerBox.textContent = "URL inválida. Configura en el panel de formato: http://servidor/api/dax/chat?ws=WORKSPACE&ds=DATASET";
            return;
        }

        if (!workspaceName || !datasetName) {
            this.answerBox.textContent = "La URL debe incluir ?ws=WORKSPACE&ds=DATASET";
            return;
        }

        this.button.disabled = true;
        this.loading.style.display = "block";
        this.answerBox.textContent = "";

        try {
            const response = await fetch(baseUrl, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ question, workspaceName, datasetName })
            });

            if (!response.ok) {
                const err = await response.text();
                this.answerBox.textContent = "Error: " + err;
                return;
            }

            const data = await response.json();
            this.answerBox.textContent = data.answer ?? JSON.stringify(data, null, 2);
        } catch (e) {
            this.answerBox.textContent = "Error de conexión con la API.";
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
        if (!this.input) {
            this.buildUI();
        }
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
