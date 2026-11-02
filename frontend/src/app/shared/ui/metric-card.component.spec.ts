import { TestBed } from '@angular/core/testing';
import { MetricCardComponent } from './metric-card.component';

describe('MetricCardComponent', () => {
  it('renders the label, value and detail', () => {
    const fixture = TestBed.createComponent(MetricCardComponent);
    fixture.componentRef.setInput('label', 'Nœuds');
    fixture.componentRef.setInput('value', '3');
    fixture.componentRef.setInput('detail', '2 workers actifs');
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.metric-label')!.textContent).toBe('Nœuds');
    expect(element.querySelector('.metric-value')!.textContent).toBe('3');
    expect(element.querySelector('.metric-detail')!.textContent).toBe('2 workers actifs');
  });
});
