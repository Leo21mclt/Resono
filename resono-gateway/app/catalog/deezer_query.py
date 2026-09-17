SEARCH_FULL_QUERY = """query SearchFull($query: String!, $firstGrid: Int!, $firstList: Int!) {
  instantSearch(query: $query) {
    bestResult {
      __typename
      ... on InstantSearchAlbumBestResult {
        album {
          ...SearchAlbum
          __typename
        }
        __typename
      }
      ... on InstantSearchArtistBestResult {
        artist {
          ...BestResultArtist
          __typename
        }
        __typename
      }
      ... on InstantSearchTrackBestResult {
        foundByLyrics
        track {
          ...TableTrack
          __typename
        }
        __typename
      }
    }
    results {
      artists(first: $firstGrid) {
        edges {
          node {
            ...SearchArtist
            __typename
          }
          __typename
        }
        pageInfo {
          endCursor
          __typename
        }
        priority
        __typename
      }
      albums(first: $firstGrid) {
        edges {
          node {
            ...SearchAlbum
            __typename
          }
          __typename
        }
        pageInfo {
          endCursor
          __typename
        }
        priority
        __typename
      }
      tracks(first: $firstList) {
        edges {
          node {
            ...TableTrack
            __typename
          }
          __typename
        }
        pageInfo {
          endCursor
          __typename
        }
        priority
        __typename
      }
      __typename
    }
    __typename
  }
}

fragment SearchAlbum on Album {
  id
  displayTitle
  releaseDateAlbum: releaseDate
  isExplicitAlbum: isExplicit
  cover {
    ...PictureLarge
    __typename
  }
  contributors {
    edges {
      roles
      node {
        ... on Artist {
          id
          name
          __typename
        }
        __typename
      }
      __typename
    }
    __typename
  }
  tracksCount
  __typename
}

fragment PictureLarge on Picture {
  id
  large: urls(pictureRequest: {width: 500, height: 500})
  explicitStatus
  __typename
}

fragment BestResultArtist on Artist {
  ...SearchArtist
  hasSmartRadio
  hasTopTracks
  __typename
}

fragment SearchArtist on Artist {
  id
  name
  fansCount
  picture {
    ...PictureLarge
    __typename
  }
  __typename
}

fragment TableTrack on Track {
  id
  title
  duration
  popularity
  isExplicit
  album {
    id
    displayTitle
    cover {
      ...PictureLarge
      __typename
    }
    __typename
  }
  contributors {
    edges {
      node {
        ... on Artist {
          id
          name
          __typename
        }
        __typename
      }
      __typename
    }
    __typename
  }
  __typename
}
"""

