/// <reference path="..\..\GSMyAdmin\WebRoot\Scripts\UI.js" />

this.plugin = {
    PushedMessage: function (source, message, parameters) {
        switch (message) {
            case "portconflict":
                UI.ShowModalAsync("UniFi port conflict", parameters, UI.Icons.Exclamation, UI.OKActionOnly, "UniFi Port Sync", parameters);
        }
    }
};
