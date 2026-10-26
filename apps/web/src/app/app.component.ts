import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService } from './core/api.service';
import { Reservation, ReservationStatus, LogEntry, Connector } from './core/models';

const BADGES: Record<ReservationStatus, string> = {
  Requested: 'bg-slate-100 text-slate-700 ring-slate-300',
  Held: 'bg-amber-50 text-amber-800 ring-amber-300',
  Booked: 'bg-emerald-50 text-emerald-800 ring-emerald-300',
  Cancelled: 'bg-sky-50 text-sky-800 ring-sky-300',
  Rejected: 'bg-rose-50 text-rose-800 ring-rose-300',
};

@Component({
  selector: 'app-root',
  imports: [DatePipe],
  templateUrl: './app.component.html',
})
export class AppComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly connectors = signal<Connector[]>([]);
  readonly selectedCode = signal('');
  readonly reservations = signal<Reservation[]>([]);
  readonly log = signal<LogEntry[]>([]);
  readonly logFor = signal<string | null>(null);
  readonly error = signal('');

  readonly code = signal('');
  readonly name = signal('Central Station');
  readonly connectorType = signal('CCS2');
  readonly city = signal('Lisbon');
  readonly price = signal('900');
  readonly capacity = signal('12');

  readonly reference = signal('');
  readonly slots = signal('1');

  readonly selected = computed(() => this.connectors().find((s) => s.code === this.selectedCode()));

  ngOnInit(): void {
    void this.refresh();
    const timer = setInterval(() => void this.refresh(), 1000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
  }

  badge(status: ReservationStatus): string {
    return BADGES[status];
  }

  money(cents: number): string {
    return (cents / 100).toLocaleString('en-US', { style: 'currency', currency: 'USD' });
  }

  async refresh(): Promise<void> {
    try {
      const connectors = await this.api.connectors();
      this.connectors.set(connectors);
      if (!this.selectedCode() && connectors.length > 0) this.selectedCode.set(connectors[0].code);
      if (this.selectedCode())
        this.reservations.set(await this.api.reservations(this.selectedCode()));
      const id = this.logFor();
      if (id) this.log.set(await this.api.events(id));
    } catch {
      this.error.set('The API is not reachable.');
      return;
    }
    if (this.error() === 'The API is not reachable.') this.error.set('');
  }

  async select(code: string): Promise<void> {
    this.selectedCode.set(code);
    this.logFor.set(null);
    this.log.set([]);
    await this.refresh();
  }

  async openConnector(): Promise<void> {
    await this.run(async () => {
      const code = this.code();
      const connector = await this.api.openConnector({
        code,
        name: this.name(),
        connectorType: this.connectorType(),
        city: this.city(),
        slotsTotal: Number(this.capacity()),
        priceCents: Number(this.price()),
      });
      if (this.code() === code) this.code.set('');
      this.selectedCode.set(connector.code);
    });
  }

  async book(): Promise<void> {
    await this.run(async () => {
      const reference = this.reference();
      await this.api.book({
        connectorCode: this.selectedCode(),
        reference,
        slots: Number(this.slots()),
      });
      if (this.reference() === reference) this.reference.set('');
    });
  }

  async cancel(reservation: Reservation): Promise<void> {
    await this.run(() => this.api.cancel(reservation.id));
  }

  async showLog(reservation: Reservation): Promise<void> {
    this.logFor.set(reservation.id);
    this.log.set([]);
    await this.refresh();
  }

  canCancel(reservation: Reservation): boolean {
    return reservation.status === 'Held' || reservation.status === 'Booked';
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.error.set('');
    try {
      await action();
    } catch (e: unknown) {
      const body = (e as { error?: { title?: string } }).error;
      this.error.set(body?.title ?? 'The request failed.');
    }
    await this.refresh();
  }

  field(signalRef: { set(value: string): void }, event: Event): void {
    signalRef.set((event.target as HTMLInputElement).value);
  }
}
