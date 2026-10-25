export type Granularity = 'Day' | 'Week' | 'Month'

/** "24 Oct", "Week of 20 Oct", "Oct 2026" for a bucket start (yyyy-MM-dd). */
export function formatBucket(bucket: string, granularity: Granularity, locale: string) {
  const date = new Date(`${bucket}T00:00:00Z`)
  if (granularity === 'Month') {
    return new Intl.DateTimeFormat(locale, { month: 'short', year: 'numeric', timeZone: 'UTC' }).format(date)
  }
  return new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'short', timeZone: 'UTC' }).format(date)
}

/** Last day of a bucket, for drill-down into the activity of that day, week or month. */
export function bucketEnd(bucket: string, granularity: Granularity) {
  const date = new Date(`${bucket}T00:00:00Z`)
  if (granularity === 'Week') date.setUTCDate(date.getUTCDate() + 6)
  if (granularity === 'Month') date.setUTCMonth(date.getUTCMonth() + 1, 0)
  return date.toISOString().slice(0, 10)
}
