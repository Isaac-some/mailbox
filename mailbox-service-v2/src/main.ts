import { loadConfig, readSecretFile } from "./config.js";
import { buildContext } from "./context.js";
import { openDatabase } from "./db/index.js";
import { migrate } from "./db/migrate.js";
import { buildWeb } from "./web.js";
import { runWorker } from "./worker.js";

const command = process.argv[2] ?? "web";
const config = loadConfig();
const db = await openDatabase(config.databaseUrl);

try {
  await migrate(db);
  if (command === "migrate") process.exitCode = 0;
  else {
    const context = buildContext(db, config, readSecretFile(config.credentialKeyFile), readSecretFile(config.sessionKeyFile));
    if (command === "seed-admin") {
      if (!config.initialAdminPassword) throw new Error("INITIAL_ADMIN_PASSWORD is required for seed-admin");
      const created = await context.auth.seedAdmin(config.initialAdminUsername, config.initialAdminPassword);
      console.log(created ? "Initial admin created" : "Users already exist; no admin created");
    } else if (command === "worker") {
      const controller = new AbortController();
      process.once("SIGTERM", () => controller.abort()); process.once("SIGINT", () => controller.abort());
      await runWorker(context, controller.signal);
    } else if (command === "web") {
      const app = await buildWeb(context);
      await app.listen({ host: config.host, port: config.port });
    } else throw new Error(`Unknown command: ${command}`);
  }
} finally {
  if (command === "migrate" || command === "seed-admin") await db.close();
}
