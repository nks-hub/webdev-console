"use strict";
const electron = require("electron");
const fs = require("fs");
const path = require("path");
const os = require("os");
const portFile = path.join(os.tmpdir(), "nks-wdc-daemon.port");
let port = 50051;
let token = "";
if (fs.existsSync(portFile)) {
  const lines = fs.readFileSync(portFile, "utf-8").split("\n").filter(Boolean);
  port = parseInt(lines[0], 10) || 50051;
  token = lines[1] || "";
}
electron.contextBridge.exposeInMainWorld("daemonApi", {
  getPort: () => port,
  getToken: () => token
});
