export type SqlPrimitive = string | number | boolean | Date | Uint8Array | null;

export interface QueryResult<T> {
  rows: T[];
  rowCount: number;
}

export interface Transaction {
  query<T = Record<string, unknown>>(sql: string, params?: SqlPrimitive[]): Promise<QueryResult<T>>;
}

export interface Database extends Transaction {
  transaction<T>(run: (tx: Transaction) => Promise<T>): Promise<T>;
  close(): Promise<void>;
}
