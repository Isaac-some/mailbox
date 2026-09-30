import { mkdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { PGlite } from "@electric-sql/pglite";
import pg from "pg";
import type { Database, QueryResult, SqlPrimitive, Transaction } from "./database.js";

function normalizeParams(params: SqlPrimitive[]) {
  return params.map((value) => value instanceof Date ? value.toISOString() : value);
}

class PgliteDatabase implements Database {
  constructor(private readonly client: PGlite) {}
  async query<T = Record<string, unknown>>(sql: string, params: SqlPrimitive[] = []): Promise<QueryResult<T>> {
    const result = await this.client.query<T>(sql, normalizeParams(params));
    return { rows: result.rows, rowCount: result.affectedRows ?? result.rows.length };
  }
  transaction<T>(run: (tx: Transaction) => Promise<T>): Promise<T> {
    return this.client.transaction(async (tx) => run(new PgliteDatabase(tx as unknown as PGlite)));
  }
  async close() { await this.client.close(); }
}

class PostgresDatabase implements Database {
  constructor(private readonly pool: pg.Pool) {}
  async query<T = Record<string, unknown>>(sql: string, params: SqlPrimitive[] = []): Promise<QueryResult<T>> {
    const result = await this.pool.query(sql, params) as pg.QueryResult<any>;
    return { rows: result.rows, rowCount: result.rowCount ?? result.rows.length };
  }
  async transaction<T>(run: (tx: Transaction) => Promise<T>): Promise<T> {
    const client = await this.pool.connect();
    try {
      await client.query("BEGIN");
      const tx: Transaction = { query: async <R = Record<string, unknown>>(sql: string, params: SqlPrimitive[] = []) => {
        const result = await client.query<R & pg.QueryResultRow>(sql, params);
        return { rows: result.rows, rowCount: result.rowCount ?? result.rows.length };
      }};
      const value = await run(tx);
      await client.query("COMMIT");
      return value;
    } catch (error) {
      await client.query("ROLLBACK");
      throw error;
    } finally { client.release(); }
  }
  async close() { await this.pool.end(); }
}

export async function openDatabase(url: string): Promise<Database> {
  if (url.startsWith("pglite://")) {
    const path = resolve(url.slice("pglite://".length));
    await mkdir(dirname(path), { recursive: true });
    return new PgliteDatabase(new PGlite(path));
  }
  return new PostgresDatabase(new pg.Pool({ connectionString: url, max: 12, idleTimeoutMillis: 30_000 }));
}
