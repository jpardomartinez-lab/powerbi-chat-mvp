"use strict";

import { formattingSettings } from "powerbi-visuals-utils-formattingmodel";

import FormattingSettingsCard = formattingSettings.SimpleCard;
import FormattingSettingsSlice = formattingSettings.Slice;
import FormattingSettingsModel = formattingSettings.Model;

class ApiSettingsCard extends FormattingSettingsCard {
    apiUrl = new formattingSettings.TextInput({
        name: "apiUrl",
        displayName: "URL de la API (incluye ?ws=WORKSPACE&ds=DATASET)",
        placeholder: "http://localhost:5211/api/dax/chat?ws=WORKSPACE&ds=DATASET",
        value: ""
    });

    name: string = "apiSettings";
    displayName: string = "Configuración API";
    slices: Array<FormattingSettingsSlice> = [this.apiUrl];
}

export class VisualFormattingSettingsModel extends FormattingSettingsModel {
    apiSettings = new ApiSettingsCard();
    cards = [this.apiSettings];
}
