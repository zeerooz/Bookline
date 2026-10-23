import { describe, expect, it } from 'vitest';

import { formatDuration, formatInZone, formatMoney } from './types';

describe('money formatting', () => {
  it('treats the stored value as minor units', () => {
    // 45000 piastres is 450 pounds, not 45,000 of anything.
    expect(formatMoney(45_000, 'EGP')).toContain('450');
  });

  it('keeps the two decimal places a price needs', () => {
    expect(formatMoney(45_050, 'EGP')).toContain('450.50');
  });

  it('handles a free service', () => {
    expect(formatMoney(0, 'EGP')).toContain('0');
  });
});

describe('duration formatting', () => {
  it('shows minutes below an hour', () => {
    expect(formatDuration(30)).toBe('30 min');
    expect(formatDuration(59)).toBe('59 min');
  });

  it('shows whole hours without a stray zero', () => {
    expect(formatDuration(60)).toBe('1 hr');
    expect(formatDuration(120)).toBe('2 hr');
  });

  it('combines hours and minutes', () => {
    expect(formatDuration(90)).toBe('1 hr 30 min');
    expect(formatDuration(135)).toBe('2 hr 15 min');
  });
});

describe('time zone rendering', () => {
  it('renders an instant in the business zone, not the viewer own', () => {
    const options: Intl.DateTimeFormatOptions = { dateStyle: undefined, timeStyle: 'short' };

    // Egypt is UTC+2 in winter and UTC+3 on summer time, so the same instant
    // is a different wall-clock time depending on the date. Getting this
    // wrong by an hour is exactly the bug a booking product cannot have.
    expect(formatInZone('2026-01-15T07:00:00Z', 'Africa/Cairo', options)).toMatch(/\b9[:.]00|09[:.]00/);
    expect(formatInZone('2026-06-01T07:00:00Z', 'Africa/Cairo', options)).toMatch(/\b10[:.]00/);
  });

  it('gives different wall-clock times for different zones', () => {
    const options: Intl.DateTimeFormatOptions = { dateStyle: undefined, timeStyle: 'short' };

    const cairo = formatInZone('2026-06-01T07:00:00Z', 'Africa/Cairo', options);
    const utc = formatInZone('2026-06-01T07:00:00Z', 'UTC', options);

    expect(cairo).not.toBe(utc);
  });
});
