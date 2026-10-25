/** IANA time zones the browser knows, plus UTC (which some browsers leave out of the list). */
export function allTimeZones(): string[] {
  const zones = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : []
  return zones.includes('UTC') ? zones : ['UTC', ...zones]
}

/** The browser's time zone, e.g. "Europe/Berlin"; undefined if it cannot tell. */
export function browserTimeZone(): string | undefined {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || undefined
  } catch {
    return undefined
  }
}

export interface TimeZoneOption {
  id: string
  /** "Berlin" from "Europe/Berlin", "Argentina / Buenos Aires" from "America/Argentina/Buenos_Aires". */
  city: string
  /** "Europe", "America" … empty for UTC. */
  region: string
  /** "GMT+2" */
  offset: string
  /** Minutes east of UTC, for sorting. */
  offsetMinutes: number
}

function offsetOf(zone: string, at: Date): { label: string; minutes: number } {
  const label =
    new Intl.DateTimeFormat('en-US', { timeZone: zone, timeZoneName: 'shortOffset' })
      .formatToParts(at)
      .find((part) => part.type === 'timeZoneName')?.value ?? 'GMT'
  const match = /GMT([+-])(\d{1,2})(?::(\d{2}))?/.exec(label)
  const minutes = match ? (match[1] === '-' ? -1 : 1) * (Number(match[2]) * 60 + Number(match[3] ?? 0)) : 0
  // Engines write UTC itself as "GMT" or "GMT+0".
  return { label: minutes === 0 ? 'GMT±0' : label, minutes }
}

export function describeTimeZone(id: string, at: Date = new Date()): TimeZoneOption {
  const parts = id.split('/')
  const region = parts.length > 1 ? (parts[0] ?? '') : ''
  const city = (parts.length > 1 ? parts.slice(1) : parts).join(' / ').replaceAll('_', ' ')
  const { label, minutes } = offsetOf(id, at)
  return { id, city, region, offset: label, offsetMinutes: minutes }
}

/** The current time in a zone, e.g. "14:32". */
export function timeIn(zone: string, locale: string, at: Date = new Date()): string {
  return new Intl.DateTimeFormat(locale, { timeZone: zone, hour: '2-digit', minute: '2-digit' }).format(at)
}

/** Case-insensitive match on id, city and offset ("berlin", "buenos aires", "gmt+2"). */
export function matchesTimeZone(option: TimeZoneOption, query: string): boolean {
  const needle = query.trim().toLowerCase()
  if (!needle) return true
  return [option.id.replaceAll('_', ' '), option.city, option.offset].some((text) => text.toLowerCase().includes(needle))
}
