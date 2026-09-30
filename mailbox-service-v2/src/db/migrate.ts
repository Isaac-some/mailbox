import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import type { Database } from "./database.js";

export async function migrate(db: Database) {
  const path = fileURLToPath(new URL("./schema.sql", import.meta.url));
  // PGlite (and PostgreSQL prepared statements) accept one command per query.
  // The schema is deliberately DDL-only, so semicolon splitting is sufficient here.
  const statements = (await readFile(path, "utf8"))
    .split(";")
    .map((statement) => statement.trim())
    .filter(Boolean);
  for (const statement of statements) await db.query(statement);
}
