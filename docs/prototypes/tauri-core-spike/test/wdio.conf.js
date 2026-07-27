// PROTOTYPE ONLY -- Wayfinder ticket #372. WebdriverIO config for the
// automated desktop UI test hard gate (scorecard "A real automated test
// passes"). Uses the embedded WebDriver provider (macOS: built-in, no
// external tauri-driver needed; Windows: switch driverProvider to 'official'
// with Microsoft Edge WebDriver -- see README.md's Windows package).
//
// Runs against the RELEASE binary (matches the scorecard's "Release, not
// Debug build configuration" measurement-environment rule).
export const config = {
  runner: "local",
  specs: ["./specs/**/*.spec.js"],
  maxInstances: 1,

  services: [
    [
      "@wdio/tauri-service",
      {
        appBinaryPath: "./target/release/tauri-app",
        driverProvider: "embedded",
      },
    ],
  ],

  capabilities: [
    {
      browserName: "tauri",
      "tauri:options": {
        application: "./target/release/tauri-app",
      },
    },
  ],

  logLevel: "info",
  bail: 0,
  waitforTimeout: 10000,
  connectionRetryTimeout: 90000,
  connectionRetryCount: 3,

  framework: "mocha",
  mochaOpts: {
    ui: "bdd",
    timeout: 60000,
  },

  reporters: ["spec"],
};
