export interface Connector {
  id: string;
  code: string;
  name: string;
  connectorType: string;
  city: string;
  serviceDate: string;
  priceCents: number;
  slotsTotal: number;
  slotsLeft: number;
}

export type ReservationStatus = 'Requested' | 'Held' | 'Booked' | 'Cancelled' | 'Rejected';

export interface Reservation {
  id: string;
  connectorCode: string;
  reference: string;
  slots: number;
  totalCents: number;
  status: ReservationStatus;
  reason: string | null;
  tokenNumber: string | null;
  createdAt: string;
  updatedAt: string;
  slotsReleasedAt: string | null;
}

export interface LogEntry {
  id: string;
  type: string;
  module: string;
  occurredAt: string;
}
