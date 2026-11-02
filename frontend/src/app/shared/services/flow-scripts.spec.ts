import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FlowScripts } from './flow-scripts';

describe('FlowScripts import et export', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    return { service: TestBed.inject(FlowScripts), http: TestBed.inject(HttpTestingController) };
  }

  it('envoie le fichier en corps brut avec son nom encodé', async () => {
    const { service, http } = setup();
    const file = new File(['print(1)'], 'track fix.py');
    const pending = service.importFile(file);
    const request = http.expectOne('/api/v1/scripts/import?name=track%20fix.py');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toBe(file);
    request.flush({ created: true, script: {} });
    expect((await pending).created).toBe(true);
  });

  it("demande l'export d'une version et d'un lot en POST", async () => {
    const { service, http } = setup();
    const one = service.exportVersion('s1', 2);
    http.expectOne('/api/v1/scripts/s1/versions/2/export').flush(new Blob(['x']));
    await one;
    const many = service.exportMany(['a', 'b']);
    const request = http.expectOne('/api/v1/scripts/export');
    expect(request.request.body).toEqual({ items: [{ id: 'a' }, { id: 'b' }] });
    request.flush(new Blob(['z']));
    await many;
  });
});
