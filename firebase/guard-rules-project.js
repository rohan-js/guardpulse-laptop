// CI guard: never deploy the laptop Firebase rules to the wrong project, and never
// let an Android-only ruleset (which lacks the laptop control/v2 extensions) ship to
// the laptop project (guardpulse-laptop-control).
//
// Fails when:
//   1. .firebaserc default is not "guardpulse-laptop-control", or
//   2. firebase/database.rules.json has no control/v2 with the laptop-only
//      "budget" and "customBlockedDomains" keys.
//
// Run: node firebase/guard-rules-project.js

const fs = require("fs");
const path = require("path");

const EXPECTED_PROJECT = "guardpulse-laptop-control";
const root = path.resolve(__dirname, "..");

function fail(message) {
  console.error("GUARD FAILED: " + message);
  process.exit(1);
}

const firebaserc = JSON.parse(fs.readFileSync(path.join(root, ".firebaserc"), "utf8"));
const defaultProject = (firebaserc.projects || {}).default;
if (defaultProject !== EXPECTED_PROJECT) {
  fail(
    `.firebaserc default is "${defaultProject}" — expected "${EXPECTED_PROJECT}". ` +
      "Deploying from this repo would target the wrong Firebase project."
  );
}

const rules = JSON.parse(fs.readFileSync(path.join(root, "firebase", "database.rules.json"), "utf8"));
const controlV2 =
  rules.rules &&
  rules.rules.devices &&
  rules.rules.devices["$deviceId"] &&
  rules.rules.devices["$deviceId"].control &&
  rules.rules.devices["$deviceId"].control.v2;
if (!controlV2) {
  fail("firebase/database.rules.json has no devices/$deviceId/control/v2 node.");
}

if (!controlV2.budget || !controlV2.customBlockedDomains) {
  fail(
    "control/v2 is missing the laptop-only keys (budget / customBlockedDomains). " +
      "This looks like the Android ruleset — never deploy it to " + EXPECTED_PROJECT + "."
  );
}

console.log("Guard OK: rules target " + EXPECTED_PROJECT + " with the laptop schema.");
