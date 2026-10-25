import { describe, expect, it } from 'vitest'
import { allTimeZones, describeTimeZone, matchesTimeZone, timeIn } from './timeZones'

describe('time zones', () => {
  const summer = new Date('2026-07-01T12:00:00Z')

  it('lists UTC and the IANA zones', () => {
    const zones = allTimeZones()
    expect(zones).toContain('UTC')
    expect(zones).toContain('Europe/Berlin')
  })

  it('describes a zone with city, region and offset', () => {
    expect(describeTimeZone('America/Argentina/Buenos_Aires', summer)).toMatchObject({
      city: 'Argentina / Buenos Aires',
      region: 'America',
      offset: 'GMT-3',
      offsetMinutes: -180,
    })
    expect(describeTimeZone('Asia/Kolkata', summer)).toMatchObject({ offset: 'GMT+5:30', offsetMinutes: 330 })
    expect(describeTimeZone('UTC', summer)).toMatchObject({ city: 'UTC', region: '', offset: 'GMT±0', offsetMinutes: 0 })
  })

  it('finds zones by city, id and offset', () => {
    const berlin = describeTimeZone('Europe/Berlin', summer)
    expect(matchesTimeZone(berlin, 'berl')).toBe(true)
    expect(matchesTimeZone(berlin, 'europe/')).toBe(true)
    expect(matchesTimeZone(berlin, 'gmt+2')).toBe(true)
    expect(matchesTimeZone(berlin, 'paris')).toBe(false)
    expect(matchesTimeZone(describeTimeZone('America/New_York', summer), 'new york')).toBe(true)
  })

  it('shows the time there', () => {
    expect(timeIn('Europe/Berlin', 'de', summer)).toBe('14:00')
  })
})
