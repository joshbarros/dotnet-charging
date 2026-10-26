import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Reservation, LogEntry, Connector } from './models';

export interface NewConnector {
  code: string;
  name: string;
  connectorType: string;
  city: string;
  slotsTotal: number;
  priceCents: number;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  connectors = () => firstValueFrom(this.http.get<Connector[]>('/api/connectors'));
  openConnector = (body: NewConnector) =>
    firstValueFrom(this.http.post<Connector>('/api/connectors', body));

  reservations = (connectorCode: string) =>
    firstValueFrom(
      this.http.get<Reservation[]>('/api/reservations', { params: { connectorCode } }),
    );
  book = (body: { connectorCode: string; reference: string; slots: number }) =>
    firstValueFrom(this.http.post<Reservation>('/api/reservations', body));
  cancel = (id: string) =>
    firstValueFrom(this.http.post<Reservation>(`/api/reservations/${id}/cancellation`, {}));
  events = (id: string) =>
    firstValueFrom(this.http.get<LogEntry[]>(`/api/reservations/${id}/events`));
}
