"use strict";

import { formattingSettings } from "powerbi-visuals-utils-formattingmodel";

import FormattingSettingsCard = formattingSettings.SimpleCard;
import FormattingSettingsSlice = formattingSettings.Slice;
import FormattingSettingsModel = formattingSettings.Model;

class ApiSettingsCard extends FormattingSettingsCard {
    apiUrl = new formattingSettings.TextInput({
        name: "apiUrl",
        displayName: "URL de la API",
        placeholder: "http://localhost:5211/api/dax/chat",
        value: "http://localhost:5211/api/dax/chat"
    });

    workspaceName = new formattingSettings.TextInput({
        name: "workspaceName",
        displayName: "Workspace Power BI",
        placeholder: "Nombre del workspace",
        value: ""
    });

    datasetName = new formattingSettings.TextInput({
        name: "datasetName",
        displayName: "Dataset / Modelo",
        placeholder: "Nombre del dataset",
        value: ""
    });

    name: string = "apiSettings";
    displayName: string = "Configuración API";
    slices: Array<FormattingSettingsSlice> = [this.apiUrl, this.workspaceName, this.datasetName];
}

export class VisualFormattingSettingsModel extends FormattingSettingsModel {
    apiSettings = new ApiSettingsCard();
    cards = [this.apiSettings];
}
