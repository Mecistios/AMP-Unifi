/// <reference path="..\..\GSMyAdmin\WebRoot\Scripts\UI.js" />

this.plugin = {
    PushedMessage: function (source, message, parameters) {
        var kinds = {
            portconflict: ["UniFi port conflict", UI.Icons.Exclamation],
            testok: ["UniFi connection test", UI.Icons.Info],
            testfail: ["UniFi connection test", UI.Icons.Exclamation],
            syncok: ["UniFi port sync", UI.Icons.Info],
            syncfail: ["UniFi port sync", UI.Icons.Exclamation]
        };
        var kind = kinds[message];
        if (!kind) { return; }
        UI.ShowModalAsync(kind[0], parameters, kind[1], UI.OKActionOnly);
        // The modal escapes markup and collapses newlines; pre-line keeps our line breaks.
        $("#modalmessage").css("white-space", "pre-line");
    }
};
