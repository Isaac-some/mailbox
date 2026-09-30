declare module "mailparser" {
  export function simpleParser(source: Uint8Array, options?: Record<string, unknown>): Promise<any>;
}
