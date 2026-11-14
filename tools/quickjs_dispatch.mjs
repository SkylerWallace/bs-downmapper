import directions from "./plan_directions.cjs";
import parity from "./check_parity.cjs";
import * as std from "qjs:std";

const [, task] = scriptArgs;
const input = JSON.parse(std.in.readAsString());
let result;
switch (task) {
  case "plan_directions":
    result = directions.solve(input);
    break;
  case "check_parity":
    if (!(input.bpm > 0)) throw new Error("Positive BPM required");
    const domains = input.repairDomains && Object.fromEntries(
      input.repairDomains.map(([beat, hand, directions]) => [hand + "/" + beat, directions]),
    );
    result = domains
      ? parity.polish(input.chart, input.bpm, input.reentries, domains)
      : parity.audit(input.chart, false, input.bpm, false, input.reentries);
    break;
  default:
    throw new Error(`Unknown BS Downmapper task ${task}`);
}
std.out.puts(JSON.stringify(result, null, 2));
