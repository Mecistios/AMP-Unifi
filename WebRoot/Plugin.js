/// <reference path="..\..\GSMyAdmin\WebRoot\Scripts\UI.js" />

this.plugin = {
    PushedMessage: function (source, message, parameters) {
        switch (message) {
            case "portconflict":
                // One conflict per line reads a lot better in the modal than a wall of text.
                var text = (parameters || "").split("\n\n").join("<br/><br/>");
                UI.ShowModalAsync("UniFi port conflict", text, UI.Icons.Exclamation, UI.OKActionOnly, "UniFi Port Sync", text);
                break;
        }
    }
};
