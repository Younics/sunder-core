const UTC_TIMESTAMP = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$/u;

export function formatUtcTimestamp(value: Date): string {
  const milliseconds = value.getTime();
  if (!Number.isFinite(milliseconds)) throw new RangeError("UTC timestamp must be a valid Date.");
  const iso = value.toISOString();
  if (!/^\d{4}-/u.test(iso) || iso.startsWith("0000-")) throw new RangeError("UTC timestamp year must be between 0001 and 9999.");
  return `${iso.slice(0, -1)}0000Z`;
}

export function parseUtcTimestamp(value: string): number {
  if (!UTC_TIMESTAMP.test(value) || value.startsWith("0000-")) return Number.NaN;
  const millisecondTimestamp = `${value.slice(0, 23)}Z`;
  const milliseconds = Date.parse(millisecondTimestamp);
  return Number.isFinite(milliseconds) && new Date(milliseconds).toISOString() === millisecondTimestamp
    ? milliseconds
    : Number.NaN;
}
