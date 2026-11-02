import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FlowResources } from './flow-resources';

describe('FlowResources', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    return {
      service: TestBed.inject(FlowResources),
      http: TestBed.inject(HttpTestingController),
    };
  }

  it('envoie un fichier en corps brut avec son nom encodé, puis le référence par empreinte', async () => {
    const { service, http } = setup();
    const file = new File([new Uint8Array([0, 1, 0, 0])], 'Trebuchet MS Bold.ttf');
    const upload = service.uploadFile(file);
    const request = http.expectOne(
      '/api/v1/resources/blobs?kind=font&name=Trebuchet%20MS%20Bold.ttf',
    );
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toBe(file);
    const stored = { name: 'Trebuchet MS Bold.ttf', sha256: 'a'.repeat(64), bytes: 4 };
    request.flush(stored);
    expect(await upload).toEqual(stored);

    const created = service.create('Crunchyroll', '', [
      { name: stored.name, sha256: stored.sha256 },
    ]);
    const creation = http.expectOne('/api/v1/resources');
    expect(creation.request.body).toEqual({
      kind: 'font',
      name: 'Crunchyroll',
      description: '',
      files: [{ name: stored.name, sha256: stored.sha256 }],
    });
    creation.flush({ summary: { id: 'x' }, versions: [] });
    await created;
    http.verify();
  });
});
